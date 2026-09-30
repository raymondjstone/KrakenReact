using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class RebalanceJob
{
    /// <summary>Quote/cash assets. They can appear in a target list ("USD:30") as the share to leave uninvested, but there is
    /// nothing to trade for them — they are simply what buys spend and sells produce.</summary>
    private static readonly HashSet<string> CashAssets = new(StringComparer.OrdinalIgnoreCase)
        { "USD", "USDT", "USDC", "GBP", "EUR", "CAD", "AUD", "JPY", "CHF" };

    private readonly TradingStateService _state;
    private readonly IOrderGateway _kraken;
    private readonly INotifier _notify;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<RebalanceJob> _logger;

    public RebalanceJob(TradingStateService state, IOrderGateway kraken, INotifier notify,
        IDbContextFactory<KrakenDbContext> dbFactory, ILogger<RebalanceJob> logger)
    {
        _state = state;
        _kraken = kraken;
        _notify = notify;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    // No automatic retry: the orders are placed before the result is saved, so a retry after a failed save would rebalance a
    // second time. The lock stops a scheduled run and a "run now" from overlapping and double-trading.
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    public async Task ExecuteAsync(int scheduleId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var schedule = await db.RebalanceSchedules.FindAsync([scheduleId], ct);
        if (schedule == null || !schedule.Active) return;

        try
        {
            var rows = CalculateRebalance(schedule.Targets);
            if (rows.Count == 0)
            {
                schedule.LastRunResult = "No balances found for targets";
                schedule.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var needsRebalance = rows.Any(r => Math.Abs(r.DriftPct) >= schedule.DriftMinPct);
            if (!needsRebalance)
            {
                schedule.LastRunResult = $"All within {schedule.DriftMinPct}% drift — no action";
                schedule.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var summary = string.Join(", ", rows
                .Where(r => Math.Abs(r.DriftPct) >= schedule.DriftMinPct)
                .Select(r => $"{r.Asset}: {r.Action} ${Math.Abs(r.DiffUsd):F0} ({r.DriftPct:+0.#;-0.#;0}% drift)"));

            if (!schedule.AutoExecute || _state.DryRunJobs)
            {
                var prefix = _state.DryRunJobs ? "DRY RUN — " : "";
                await _notify.Pushover($"{prefix}Rebalance Alert", summary);
                schedule.LastRunResult = $"{(_state.DryRunJobs ? "DRY RUN — " : "")}Alert sent: {summary}";
                schedule.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            if (!_state.IsPriceFeedAlive())
            {
                schedule.LastRunResult = "Skipped — live price feed is not delivering, so orders would be priced off stale data";
                schedule.LastRunAt = DateTime.UtcNow;
                await _notify.Pushover("Rebalance skipped", schedule.LastRunResult);
                await db.SaveChangesAsync(ct);
                return;
            }

            var errors = new List<string>();
            var cash = _state.Balances.TryGetValue("USD", out var usd) ? usd.Available : 0m;

            // Sells first: they are what free the cash the buys spend
            var toTrade = rows
                .Where(r => Math.Abs(r.DriftPct) >= schedule.DriftMinPct && r.Action != "HOLD" && !CashAssets.Contains(r.Asset))
                .OrderBy(r => r.Action == "SELL" ? 0 : 1);

            foreach (var row in toTrade)
            {
                var sym = FindSymbol(row.Asset);
                if (sym == null) { errors.Add($"{row.Asset}: no symbol found"); continue; }

                var side = row.Action == "BUY" ? OrderSide.Buy : OrderSide.Sell;
                var price = row.CurrentPrice;
                if (price <= 0) continue;
                var qty = Math.Abs(row.DiffQty);

                if (side == OrderSide.Sell)
                {
                    // Can't sell coins that are already committed to other orders
                    var available = _state.Balances.TryGetValue(row.Asset, out var held) ? held.Available : 0m;
                    if (qty > available)
                    {
                        _logger.LogInformation("[Rebalance] {Asset}: selling {Available} rather than {Wanted} (the rest is in open orders)", row.Asset, available, qty);
                        qty = available;
                    }
                }
                else
                {
                    // Never spend more cash than is free
                    var cost = qty * price;
                    if (cost > cash)
                    {
                        qty = KrakenReact.Server.Utils.DecimalMath.FloorToDecimals(cash / price, 8);
                        _logger.LogInformation("[Rebalance] {Asset}: buying {Qty} rather than the full amount (only ${Cash:F2} free)", row.Asset, qty, cash);
                    }
                }

                if (qty <= 0)
                {
                    errors.Add($"{row.Asset}: nothing available to {(side == OrderSide.Sell ? "sell" : "spend")}");
                    continue;
                }

                var clientId = KrakenReact.Server.Utils.ClientOrderId.GenerateTimestampWithPrefix($"REB{scheduleId}_{row.Asset}_");
                var result = await _kraken.PlaceOrderWithRecoveryAsync(sym, side, OrderType.Limit, qty, price, clientId, postOnly: false); // rebalancing wants the fill, not a resting order
                if (result.Unknown)
                    errors.Add($"{row.Asset}: UNCONFIRMED ({result.Error}) — check Kraken");
                else if (!result.Success)
                    errors.Add($"{row.Asset}: {result.Error}");
                else if (side == OrderSide.Buy)
                    cash -= qty * price;
            }

            schedule.LastRunResult = errors.Any()
                ? $"Partial — errors: {string.Join("; ", errors)}"
                : $"OK — rebalanced: {summary}";
            await _notify.Pushover(errors.Any() ? "Rebalance Partially Executed" : "Rebalance Executed", schedule.LastRunResult);
        }
        catch (Exception ex)
        {
            schedule.LastRunResult = $"Exception: {ex.Message}";
            _logger.LogError(ex, "[Rebalance] Exception for schedule {Id}", scheduleId);
        }

        schedule.LastRunAt = DateTime.UtcNow;
        // Must not throw: the orders may already be on Kraken, and nothing should re-run them because the result couldn't be saved
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex) { _logger.LogCritical(ex, "[Rebalance] Could not record the result of schedule {Id}: {Result}", scheduleId, schedule.LastRunResult); }
    }

    private List<RebalanceRow> CalculateRebalance(string targets)
    {
        var targetMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in targets.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 && decimal.TryParse(kv[1], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var pct))
                targetMap[kv[0].Trim()] = pct;
        }

        var balances = _state.Balances.Values.Where(b => b.Total > 0 && b.LatestValue > 0).ToList();
        var totalUsd = balances.Sum(b => b.LatestValue);
        if (totalUsd <= 0) return [];

        return targetMap.Select(kvp =>
        {
            var asset = kvp.Key;
            var targetPct = kvp.Value;
            var bal = balances.FirstOrDefault(b => string.Equals(b.Asset, asset, StringComparison.OrdinalIgnoreCase));
            var currentUsd = bal?.LatestValue ?? 0m;
            var currentPct = totalUsd > 0 ? currentUsd / totalUsd * 100m : 0m;
            var driftPct = currentPct - targetPct;
            var targetUsd = totalUsd * targetPct / 100m;
            var diffUsd = targetUsd - currentUsd;
            var price = bal?.LatestPrice ?? 0m;
            var diffQty = price > 0 ? diffUsd / price : 0m;
            return new RebalanceRow(asset, targetPct, currentPct, driftPct, diffUsd, diffQty, price,
                diffUsd > 0 ? "BUY" : diffUsd < 0 ? "SELL" : "HOLD");
        }).ToList();
    }

    private string? FindSymbol(string asset)
    {
        var sym = _state.Symbols.Values.FirstOrDefault(s =>
            TradingStateService.NormalizeAsset(s.BaseAsset) == asset && s.WebsocketName.EndsWith("/USD"));
        return sym?.WebsocketName.Replace("/", "");
    }

    private record RebalanceRow(string Asset, decimal TargetPct, decimal CurrentPct, decimal DriftPct,
        decimal DiffUsd, decimal DiffQty, decimal CurrentPrice, string Action);
}
