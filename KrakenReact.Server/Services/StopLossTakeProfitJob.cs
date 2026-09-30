using System.Text.Json;
using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class StopLossTakeProfitJob
{
    /// <summary>AppSettings key: comma-separated assets the stop-loss / take-profit / trailing-stop checks must never sell
    /// (long-term holdings, staked coins…). Matched after asset-name normalization, e.g. "BTC,ETH".</summary>
    public const string ExcludedAssetsKey = "ProtectionExcludedAssets";

    /// <summary>AppSettings key holding the trailing-stop high-water marks as JSON, so a restart doesn't reset them.</summary>
    public const string TrailingHighsKey = "TrailingHighPrices";

    private static bool _trailingHighsLoaded;

    private readonly TradingStateService _state;
    private readonly IOrderGateway _kraken;
    private readonly INotifier _notify;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<StopLossTakeProfitJob> _logger;
    private readonly SqlTimeoutDiagnostics _sqlDiag;
    private static readonly HashSet<string> FIAT = new(StringComparer.OrdinalIgnoreCase)
        { "USD", "USDT", "USDC", "GBP", "EUR", "CAD", "AUD", "JPY", "CHF" };

    public StopLossTakeProfitJob(TradingStateService state, IOrderGateway kraken, INotifier notify,
        IDbContextFactory<KrakenDbContext> dbFactory, ILogger<StopLossTakeProfitJob> logger,
        SqlTimeoutDiagnostics sqlDiag)
    {
        _state = state;
        _kraken = kraken;
        _notify = notify;
        _dbFactory = dbFactory;
        _logger = logger;
        _sqlDiag = sqlDiag;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        if (_sqlDiag.RecentTimeout(SqlTimeoutDiagnostics.RecentTimeoutBackoff))
        {
            _logger.LogWarning("[StopLoss] Skipping tick — recent SQL timeout elsewhere, backing off");
            return;
        }

        // Every decision below compares a price to a threshold. If the live feed has gone quiet the "latest price" is
        // whatever it was when it died, so do nothing rather than sell (or fail to sell) against a market that has moved on.
        if (!_state.IsPriceFeedAlive())
        {
            _logger.LogWarning("[StopLoss] Skipping tick — no price ticks for {Age}", _state.FeedAge?.ToString() ?? "ever");
            await NotifyFeedStaleAsync();
            return;
        }

        // The three checks are independent: a failure in one must not stop the others running this tick
        await RunCheckAsync("StopLoss", () => CheckStopLossTakeProfitAsync(ct));
        await RunCheckAsync("TrailingStop", () => CheckTrailingStopAsync(ct));
        await RunCheckAsync("ProfitLadder", () => CheckProfitLadderAsync(ct));
    }

    private static DateTime _feedStaleNotifiedAt = DateTime.MinValue;

    /// <summary>Tells the user their protection is paused — at most once an hour, so a long outage isn't a flood.</summary>
    private async Task NotifyFeedStaleAsync()
    {
        if (DateTime.UtcNow - _feedStaleNotifiedAt < TimeSpan.FromHours(1)) return;
        _feedStaleNotifiedAt = DateTime.UtcNow;
        await _notify.Pushover("Price feed stalled — protection paused",
            "No live prices have arrived for over 5 minutes, so stop-loss / take-profit / trailing stop are not running until the feed recovers.");
    }

    private async Task RunCheckAsync(string name, Func<Task> check)
    {
        try { await check(); }
        catch (Exception ex)
        {
            _sqlDiag.CaptureIfTimeout($"StopLossTakeProfitJob.{name}", ex);
            _logger.LogError(ex, "[{Check}] Check failed; the remaining checks still run", name);
        }
    }

    private async Task<HashSet<string>> LoadExcludedAssetsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var value = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ExcludedAssetsKey, ct))?.Value;
            return ParseExcludedAssets(value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StopLoss] Could not read {Key}; treating the exclusion list as empty", ExcludedAssetsKey);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static HashSet<string> ParseExcludedAssets(string? value) =>
        new((value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(TradingStateService.NormalizeAsset),
            StringComparer.OrdinalIgnoreCase);

    private async Task CheckStopLossTakeProfitAsync(CancellationToken ct)
    {
        if (!_state.StopLossEnabled && !_state.TakeProfitEnabled) return;

        var excluded = await LoadExcludedAssetsAsync(ct);
        foreach (var bal in _state.Balances.Values.ToList())
        {
            if (FIAT.Contains(bal.Asset)) continue;
            if (excluded.Contains(TradingStateService.NormalizeAsset(bal.Asset))) continue;
            if (bal.Available <= 0 || bal.LatestValue < 5m) continue;
            if (!bal.TotalCostBasis.HasValue || bal.TotalCostBasis.Value <= 0 || bal.Total <= 0) continue;

            // One asset failing (network error, rejection) must not stop the rest being protected
            try { await ProcessStopLossTakeProfitAsync(bal); }
            catch (Exception ex) { _logger.LogError(ex, "[StopLoss] Error processing {Asset}", bal.Asset); }
        }
    }

    private async Task ProcessStopLossTakeProfitAsync(BalanceDto bal)
    {
        var avgCost = bal.TotalCostBasis!.Value / bal.Total;
        var currentPrice = bal.LatestPrice;
        if (currentPrice <= 0) return;

        var changePct = (currentPrice - avgCost) / avgCost * 100m;

        if (_state.StopLossEnabled && changePct <= -_state.StopLossPct)
        {
            _logger.LogWarning("[StopLoss] {Asset} at {Price:F4}, down {Pct:F1}% from avg {Avg:F4}",
                bal.Asset, currentPrice, changePct, avgCost);

            var sym = FindSymbol(bal.Asset);
            if (sym == null) return;

            if (_state.DryRunJobs)
            {
                _logger.LogInformation("[StopLoss] DRY RUN — would market-sell {Qty} {Asset}", bal.Available, bal.Asset);
                await _notify.Pushover($"DRY RUN — Stop-Loss {bal.Asset}",
                    $"Would sell {bal.Available:F4} {bal.Asset} at market (down {Math.Abs(changePct):F1}% from avg cost {avgCost:F4})");
                return;
            }

            var clientId = KrakenReact.Server.Utils.ClientOrderId.GenerateTimestampWithPrefix("SL");
            var result = await _kraken.PlaceOrderWithRecoveryAsync(sym, OrderSide.Sell, OrderType.Market, bal.Available, 0, clientId);
            if (result.Success)
            {
                await _notify.Pushover($"Stop-Loss Triggered — {bal.Asset}",
                    $"Sold {bal.Available:F4} {bal.Asset} at market price (down {Math.Abs(changePct):F1}% from avg cost {avgCost:F4})");
                _logger.LogInformation("[StopLoss] Stop-loss order placed for {Asset}", bal.Asset);
            }
            else
            {
                _logger.LogError("[StopLoss] Failed to place stop-loss for {Asset}: {Error}", bal.Asset, result.Error);
                await _notify.Pushover(result.Unknown ? $"Stop-Loss UNCONFIRMED — {bal.Asset}" : $"Stop-Loss FAILED — {bal.Asset}",
                    result.Unknown
                        ? $"Could not confirm whether the market sell was placed ({result.Error}). Check Kraken before doing anything else."
                        : result.Error ?? "unknown error");
            }
        }
        else if (_state.TakeProfitEnabled && changePct >= _state.TakeProfitPct)
        {
            _logger.LogInformation("[TakeProfit] {Asset} at {Price:F4}, up {Pct:F1}% from avg {Avg:F4}",
                bal.Asset, currentPrice, changePct, avgCost);

            var sym = FindSymbol(bal.Asset);
            if (sym == null) return;

            if (_state.DryRunJobs)
            {
                _logger.LogInformation("[TakeProfit] DRY RUN — would limit-sell {Qty} {Asset} @ {Price}", bal.Available, bal.Asset, currentPrice);
                await _notify.Pushover($"DRY RUN — Take-Profit {bal.Asset}",
                    $"Would limit-sell {bal.Available:F4} {bal.Asset} @ {currentPrice:F4} (up {changePct:F1}% from avg {avgCost:F4})");
                return;
            }

            var clientId = KrakenReact.Server.Utils.ClientOrderId.GenerateTimestampWithPrefix("TP");
            var result = await _kraken.PlaceOrderWithRecoveryAsync(sym, OrderSide.Sell, OrderType.Limit, bal.Available, currentPrice, clientId, postOnly: false);
            if (result.Success)
            {
                await _notify.Pushover($"Take-Profit Triggered — {bal.Asset}",
                    $"Limit sell {bal.Available:F4} {bal.Asset} @ {currentPrice:F4} (up {changePct:F1}% from avg {avgCost:F4})");
                _logger.LogInformation("[TakeProfit] Take-profit order placed for {Asset}", bal.Asset);
            }
            else
            {
                _logger.LogError("[TakeProfit] Failed to place take-profit for {Asset}: {Error}", bal.Asset, result.Error);
            }
        }
    }

    private async Task CheckTrailingStopAsync(CancellationToken ct)
    {
        if (!_state.TrailingStopEnabled) return;

        if (!_trailingHighsLoaded) await LoadTrailingHighsAsync(ct);
        var excluded = await LoadExcludedAssetsAsync(ct);
        var changed = false;

        foreach (var bal in _state.Balances.Values.ToList())
        {
            if (FIAT.Contains(bal.Asset)) continue;
            if (excluded.Contains(TradingStateService.NormalizeAsset(bal.Asset))) continue;
            // A high belongs to one position. Once the asset is no longer held (sold by hand, moved away) forget it, or
            // a later re-buy at a lower price would look like an instant drop from the old high and be market-sold.
            // Held is judged on the TOTAL balance: coins resting in a sell order are still the same position.
            if (!IsPositionHeld(bal.Total, bal.LatestValue))
            {
                if (_state.TrailingHighPrices.TryRemove(bal.Asset, out _)) changed = true;
                continue;
            }
            if (bal.Available <= 0) continue;

            var currentPrice = bal.LatestPrice;
            if (currentPrice <= 0) continue;

            try
            {
                // Update tracked high
                var before = _state.TrailingHighPrices.TryGetValue(bal.Asset, out var prev) ? prev : 0m;
                _state.TrailingHighPrices.AddOrUpdate(bal.Asset, currentPrice, (_, existing) => Math.Max(existing, currentPrice));
                var high = _state.TrailingHighPrices[bal.Asset];
                if (high != before) changed = true;

                var dropPct = (high - currentPrice) / high * 100m;
                if (dropPct < _state.TrailingStopPct) continue;

                var sym = FindSymbol(bal.Asset);
                if (sym == null) continue;

                _logger.LogWarning("[TrailingStop] {Asset} dropped {Pct:F1}% from high {High:F4} (now {Price:F4})",
                    bal.Asset, dropPct, high, currentPrice);

                if (_state.DryRunJobs)
                {
                    await _notify.Pushover($"DRY RUN — Trailing Stop {bal.Asset}",
                        $"Would market-sell {bal.Available:F4} {bal.Asset} — dropped {dropPct:F1}% from high {high:F4}");
                    _state.TrailingHighPrices[bal.Asset] = currentPrice;
                    changed = true;
                    continue;
                }

                var clientId = KrakenReact.Server.Utils.ClientOrderId.GenerateTimestampWithPrefix("TS");
                var result = await _kraken.PlaceOrderWithRecoveryAsync(sym, OrderSide.Sell, OrderType.Market, bal.Available, 0, clientId);
                if (result.Success)
                {
                    await _notify.Pushover($"Trailing Stop Triggered — {bal.Asset}",
                        $"Sold {bal.Available:F4} {bal.Asset} at market — dropped {dropPct:F1}% from high {high:F4}");
                    _logger.LogInformation("[TrailingStop] Order placed for {Asset}", bal.Asset);
                    _state.TrailingHighPrices.TryRemove(bal.Asset, out _);
                    changed = true;
                }
                else
                {
                    _logger.LogError("[TrailingStop] Failed to place order for {Asset}: {Error}", bal.Asset, result.Error);
                    if (result.Unknown)
                        await _notify.Pushover($"Trailing Stop UNCONFIRMED — {bal.Asset}",
                            $"Could not confirm whether the market sell was placed ({result.Error}). Check Kraken.");
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "[TrailingStop] Error processing {Asset}", bal.Asset); }
        }

        // Drop highs for assets that no longer have a balance entry at all
        var heldAssets = _state.Balances.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in _state.TrailingHighPrices.Keys.ToList())
            if (!heldAssets.Contains(asset) && _state.TrailingHighPrices.TryRemove(asset, out _)) changed = true;

        if (changed) await SaveTrailingHighsAsync(ct);
    }

    /// <summary>True while a balance is a real position (worth more than dust).</summary>
    internal static bool IsPositionHeld(decimal total, decimal latestValueUsd) => total > 0 && latestValueUsd >= 5m;

    /// <summary>Restores the trailing-stop highs saved by an earlier run. Without this a restart reset every high to the
    /// current price, silently loosening the stop by however far the price had already fallen.</summary>
    private async Task LoadTrailingHighsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var json = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == TrailingHighsKey, ct))?.Value;
            foreach (var (asset, high) in ParseTrailingHighs(json))
                _state.TrailingHighPrices.AddOrUpdate(asset, high, (_, existing) => Math.Max(existing, high));
            _trailingHighsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TrailingStop] Could not load saved highs; will retry next tick");
        }
    }

    private async Task SaveTrailingHighsAsync(CancellationToken ct)
    {
        try
        {
            var json = SerializeTrailingHighs(_state.TrailingHighPrices);
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == TrailingHighsKey, ct);
            if (setting == null)
                db.AppSettings.Add(new AppSettings { Key = TrailingHighsKey, Value = json, Description = "Trailing-stop high-water marks (managed automatically)" });
            else
                setting.Value = json;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[TrailingStop] Could not save highs"); }
    }

    internal static Dictionary<string, decimal> ParseTrailingHighs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, decimal>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    internal static string SerializeTrailingHighs(IEnumerable<KeyValuePair<string, decimal>> highs) =>
        JsonSerializer.Serialize(highs.ToDictionary(k => k.Key, k => k.Value));

    private async Task CheckProfitLadderAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Short timeout for the polling read — tiny table, runs every 5 min;
        // failing fast frees the pool slot for jobs that need it more.
        if (db.Database.IsRelational()) // the in-memory test provider has no command timeout
            db.Database.SetCommandTimeout(TimeSpan.FromSeconds(15));
        var rules = await db.ProfitLadderRules.Where(r => r.Active).ToListAsync(ct);
        if (rules.Count == 0) return;

        var disarmedRow = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == DisarmedRulesKey, ct);
        var disarmed = ParseDisarmedRules(disarmedRow?.Value);
        var before = SerializeDisarmedRules(disarmed);

        foreach (var rule in rules)
        {
            try { await ProcessProfitLadderRuleAsync(rule, disarmed); }
            catch (Exception ex)
            {
                rule.LastResult = $"Exception: {ex.Message}";
                _logger.LogError(ex, "[ProfitLadder] Error processing rule {Id}", rule.Id);
            }
        }

        var after = SerializeDisarmedRules(disarmed);
        if (after != before)
        {
            if (disarmedRow == null)
                db.AppSettings.Add(new AppSettings { Key = DisarmedRulesKey, Value = after, Description = "Profit ladder rules that have sold and are waiting for the gain to fall back below their trigger (managed automatically)" });
            else
                disarmedRow.Value = after;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>AppSettings key holding the ids of profit-ladder rules that have fired and not yet re-armed.</summary>
    public const string DisarmedRulesKey = "ProfitLadderDisarmedRules";

    internal static HashSet<int> ParseDisarmedRules(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : -1).Where(id => id >= 0).ToHashSet();

    internal static string SerializeDisarmedRules(IEnumerable<int> ids) => string.Join(",", ids.OrderBy(i => i));

    private async Task ProcessProfitLadderRuleAsync(ProfitLadderRule rule, HashSet<int> disarmed)
    {
        var normalizedAsset = TradingStateService.NormalizeAsset(rule.Symbol.Split('/')[0]);
        var bal = _state.Balances.Values.FirstOrDefault(b =>
            string.Equals(b.Asset, normalizedAsset, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(b.Asset, rule.Symbol.Split('/')[0], StringComparison.OrdinalIgnoreCase));

        if (bal == null || bal.LatestPrice <= 0) return;
        if (!bal.TotalCostBasis.HasValue || bal.TotalCostBasis.Value <= 0 || bal.Total <= 0) return;

        var avgCost = bal.TotalCostBasis.Value / bal.Total;
        var changePct = (bal.LatestPrice - avgCost) / avgCost * 100m;

        // A rung sells once per crossing. After it fires the rule is disarmed and stays so while the gain remains at or
        // above the trigger — the cooldown alone re-sold another slice every period for as long as the price stayed high.
        // It re-arms once the gain has fallen back below the trigger, so the next crossing sells again. Checked before
        // the cooldown so a dip that happens during the cooldown isn't missed.
        if (disarmed.Contains(rule.Id))
        {
            if (changePct < rule.TriggerPct) disarmed.Remove(rule.Id);
            return;
        }

        if (rule.LastTriggeredAt.HasValue &&
            (DateTime.UtcNow - rule.LastTriggeredAt.Value).TotalHours < rule.CooldownHours)
            return;

        if (bal.Available <= 0) return;
        if (changePct < rule.TriggerPct) return;

        _logger.LogInformation("[ProfitLadder] {Asset} up {Pct:F1}% — triggering rule {Id} (sell {SellPct}%)",
            bal.Asset, changePct, rule.Id, rule.SellPct);

        // Floor rather than round (never sell more than the rule's share); PlaceOrderAsync applies the pair's lot precision
        var sellQty = KrakenReact.Server.Utils.DecimalMath.FloorToDecimals(bal.Available * rule.SellPct / 100m, 8);
        if (sellQty <= 0) return;

        var sym = FindSymbol(bal.Asset);
        if (sym == null) return;

        if (_state.DryRunJobs)
        {
            _logger.LogInformation("[ProfitLadder] DRY RUN — would limit-sell {Qty} {Asset} @ {Price}", sellQty, bal.Asset, bal.LatestPrice);
            rule.LastTriggeredAt = DateTime.UtcNow;
            rule.LastResult = $"DRY RUN — would sell {sellQty:F6} {bal.Asset} @ {bal.LatestPrice:F4} (up {changePct:F1}%)";
            disarmed.Add(rule.Id);
            await _notify.Pushover($"DRY RUN — Profit Ladder {bal.Asset}",
                $"Would limit-sell {sellQty:F6} {bal.Asset} @ {bal.LatestPrice:F4} (+{changePct:F1}% from avg cost)");
            return;
        }

        var clientId = KrakenReact.Server.Utils.ClientOrderId.GenerateTimestampWithPrefix($"PL{rule.Id}_");
        var result = await _kraken.PlaceOrderWithRecoveryAsync(sym, OrderSide.Sell, OrderType.Limit, sellQty, bal.LatestPrice, clientId, postOnly: false);

        rule.LastTriggeredAt = DateTime.UtcNow;
        rule.LastResult = result.Success
            ? $"OK — sold {sellQty:F6} {bal.Asset} @ {bal.LatestPrice:F4} (up {changePct:F1}%)"
            : $"FAIL: {result.Error ?? "unknown"}";

        // Disarm on success and on an unconfirmed outcome (the sell may exist — don't risk selling the slice twice);
        // a definite failure stays armed and is retried after the cooldown.
        if (result.Success || result.Unknown) disarmed.Add(rule.Id);

        if (result.Success)
        {
            await _notify.Pushover($"Profit Ladder Triggered — {bal.Asset}",
                $"Limit sell {sellQty:F6} {bal.Asset} @ {bal.LatestPrice:F4} (+{changePct:F1}% from avg cost)");
        }
        else
        {
            _logger.LogError("[ProfitLadder] Failed to place order for {Asset}: {Error}", bal.Asset, result.Error);
        }
    }

    private string? FindSymbol(string asset)
    {
        var key = _state.Symbols.Values.FirstOrDefault(s =>
            TradingStateService.NormalizeAsset(s.BaseAsset) == asset && s.WebsocketName.EndsWith("/USD"))
            ?.WebsocketName;
        return key?.Replace("/", "");
    }
}
