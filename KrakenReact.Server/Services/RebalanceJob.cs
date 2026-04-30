using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class RebalanceJob
{
    private readonly TradingStateService _state;
    private readonly KrakenRestService _kraken;
    private readonly NotificationService _notify;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<RebalanceJob> _logger;

    public RebalanceJob(TradingStateService state, KrakenRestService kraken, NotificationService notify,
        IDbContextFactory<KrakenDbContext> dbFactory, ILogger<RebalanceJob> logger)
    {
        _state = state;
        _kraken = kraken;
        _notify = notify;
        _dbFactory = dbFactory;
        _logger = logger;
    }

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

            var errors = new List<string>();
            foreach (var row in rows.Where(r => Math.Abs(r.DriftPct) >= schedule.DriftMinPct && r.Action != "HOLD"))
            {
                var sym = FindSymbol(row.Asset);
                if (sym == null) { errors.Add($"{row.Asset}: no symbol found"); continue; }

                var side = row.Action == "BUY" ? OrderSide.Buy : OrderSide.Sell;
                var qty = Math.Abs(row.DiffQty);
                if (qty <= 0) continue;

                var price = row.CurrentPrice;
                if (price <= 0) continue;

                var clientId = $"REB{scheduleId}_{row.Asset}_{DateTime.Now:yyyyMMddHHmm}";
                var result = await _kraken.PlaceOrderAsync(sym, side, OrderType.Limit, qty, price, clientId);
                if (!result.Success)
                    errors.Add($"{row.Asset}: {result.Error?.Message}");
            }

            schedule.LastRunResult = errors.Any()
                ? $"Partial — errors: {string.Join("; ", errors)}"
                : $"OK — rebalanced: {summary}";
            await _notify.Pushover("Rebalance Executed", schedule.LastRunResult);
        }
        catch (Exception ex)
        {
            schedule.LastRunResult = $"Exception: {ex.Message}";
            _logger.LogError(ex, "[Rebalance] Exception for schedule {Id}", scheduleId);
        }

        schedule.LastRunAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
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
