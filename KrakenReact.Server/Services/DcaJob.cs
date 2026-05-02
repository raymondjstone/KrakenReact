using System.Text.Json;
using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class DcaJob
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly NotificationService _notify;
    private readonly ILogger<DcaJob> _logger;

    // Fear & Greed cache — shared across transient instances, refreshed every 4 hours
    private static (int value, DateTime fetchedAt) _fgCache = (50, DateTime.MinValue);
    private static readonly SemaphoreSlim _fgLock = new(1, 1);
    private static readonly HttpClient _fgHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    public DcaJob(IDbContextFactory<KrakenDbContext> dbFactory, KrakenRestService kraken, TradingStateService state, NotificationService notify, ILogger<DcaJob> logger)
    {
        _dbFactory = dbFactory;
        _kraken = kraken;
        _state = state;
        _notify = notify;
        _logger = logger;
    }

    private async Task<int> GetFearGreedIndexAsync()
    {
        if ((DateTime.UtcNow - _fgCache.fetchedAt).TotalHours < 4) return _fgCache.value;

        await _fgLock.WaitAsync();
        try
        {
            if ((DateTime.UtcNow - _fgCache.fetchedAt).TotalHours < 4) return _fgCache.value;
            var json = await _fgHttp.GetStringAsync("https://api.alternative.me/fng/?limit=1");
            using var doc = JsonDocument.Parse(json);
            var valStr = doc.RootElement.GetProperty("data")[0].GetProperty("value").GetString();
            if (int.TryParse(valStr, out var idx))
            {
                _fgCache = (idx, DateTime.UtcNow);
                return idx;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DCA] Could not fetch Fear & Greed — treating as neutral (50)");
        }
        finally
        {
            _fgLock.Release();
        }
        return 50;
    }

    private static decimal ComputeAtr(IList<DerivedKline> klines, int period = 14)
    {
        if (klines.Count < period + 1) return 0;
        var sorted = klines.OrderBy(k => k.OpenTime).ToList();
        var trs = new List<decimal>(sorted.Count);
        for (int i = 1; i < sorted.Count; i++)
        {
            var h = sorted[i].High;
            var l = sorted[i].Low;
            var pc = sorted[i - 1].Close;
            trs.Add(Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc))));
        }
        return trs.TakeLast(period).Average();
    }

    [AutomaticRetry(Attempts = 1)]
    public async Task ExecuteAsync(int ruleId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rule = await db.DcaRules.FindAsync([ruleId], ct);
        if (rule == null || !rule.Active) return;

        _logger.LogInformation("[DCA] Executing rule {Id}: buy {Amt} USD of {Symbol}", ruleId, rule.AmountUsd, rule.Symbol);

        try
        {
            var latestKline = _state.LatestPrice(rule.Symbol.Split('/')[0]);
            if (latestKline == null || latestKline.Close <= 0)
            {
                rule.LastRunResult = "No price available";
                rule.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var price = Math.Round(latestKline.Close * 1.002m, 2);
            var qty = Math.Round(rule.AmountUsd / price, 6);

            // Feature: ATR-adjusted position sizing
            if (rule.AtrSizingEnabled && rule.AtrRiskUsd > 0)
            {
                if (_state.Prices.TryGetValue(rule.Symbol, out var atrPriceItem))
                {
                    var klines = atrPriceItem.GetKlineSnapshot()
                        .Where(k => k.Interval == "OneDay" && k.Close > 0 && k.High > 0 && k.Low > 0)
                        .ToList();
                    var atr = ComputeAtr(klines);
                    if (atr > 0)
                    {
                        qty = Math.Round(rule.AtrRiskUsd / atr, 6);
                        _logger.LogInformation("[DCA] ATR sizing: ATR={Atr:F4}, RiskUsd={Risk}, qty={Qty}",
                            atr, rule.AtrRiskUsd, qty);
                    }
                    else
                    {
                        _logger.LogWarning("[DCA] ATR sizing: insufficient kline data — falling back to fixed amount");
                    }
                }
                else
                {
                    _logger.LogWarning("[DCA] ATR sizing: no price data for {Symbol} — falling back to fixed amount", rule.Symbol);
                }
            }

            var sym = _state.Symbols.Values.FirstOrDefault(s =>
                s.WebsocketName.Equals(rule.Symbol, StringComparison.OrdinalIgnoreCase));

            var minQty = sym?.OrderMin ?? 0.0001m;
            if (qty < minQty)
            {
                rule.LastRunResult = $"Qty {qty} below min {minQty}";
                rule.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var minValue = sym?.MinValue ?? 0m;
            if (minValue > 0 && qty * price < minValue)
            {
                rule.LastRunResult = $"Order value {qty * price:F2} below min value {minValue}";
                rule.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            // Feature: MA conditional filter
            if (rule.ConditionalEnabled && rule.ConditionalMaPeriod > 0)
            {
                if (!_state.Prices.TryGetValue(rule.Symbol, out var priceItem))
                {
                    rule.LastRunResult = "Conditional skip — price data not available";
                    rule.LastRunAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return;
                }
                var klines = priceItem.GetKlineSnapshot()
                    .Where(k => k.Interval == "OneDay" && k.Close > 0)
                    .OrderByDescending(k => k.OpenTime)
                    .Take(rule.ConditionalMaPeriod)
                    .ToList();
                if (klines.Count >= rule.ConditionalMaPeriod)
                {
                    var ma = klines.Average(k => k.Close);
                    if (price > ma)
                    {
                        rule.LastRunResult = $"Conditional skip — price {price:F4} above {rule.ConditionalMaPeriod}-day MA {ma:F4}";
                        rule.LastRunAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                        return;
                    }
                }
            }

            // Feature: Fear & Greed index gate
            if (rule.FearGreedEnabled)
            {
                var fgIndex = await GetFearGreedIndexAsync();
                _logger.LogInformation("[DCA] Fear & Greed index: {Index} (max allowed: {Max})", fgIndex, rule.FearGreedMaxIndex);
                if (fgIndex > rule.FearGreedMaxIndex)
                {
                    rule.LastRunResult = $"Fear & Greed skip — index {fgIndex} > max {rule.FearGreedMaxIndex} (market too greedy)";
                    rule.LastRunAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return;
                }
            }

            if (_state.DryRunJobs)
            {
                rule.LastRunResult = $"DRY RUN — would buy {qty} @ {price} (${rule.AmountUsd})";
                _logger.LogInformation("[DCA] DRY RUN — would place buy: {Symbol} {Qty} @ {Price}", rule.Symbol, qty, price);
                await _notify.Pushover($"DRY RUN — DCA {rule.Symbol}", $"Would buy {qty} {rule.Symbol.Split('/')[0]} @ {price:F2} USD (${rule.AmountUsd:F2} DCA)");
                rule.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var clientId = $"dca-{ruleId}-{DateTime.UtcNow:yyyyMMddHHmm}";
            var result = await _kraken.PlaceOrderAsync(rule.Symbol, OrderSide.Buy, OrderType.Limit, qty, price, clientId);

            if (result.Success)
            {
                rule.LastRunResult = $"OK — {qty} @ {price} (orderId={result.Data?.OrderIds?.FirstOrDefault()})";
                await _notify.Pushover($"DCA Buy {rule.Symbol}", $"Bought {qty} {rule.Symbol.Split('/')[0]} @ {price:F2} USD (${rule.AmountUsd:F2} DCA)");
            }
            else
            {
                rule.LastRunResult = $"Error: {result.Error?.Message}";
                _logger.LogError("[DCA] Order failed for rule {Id}: {Error}", ruleId, result.Error?.Message);
            }
        }
        catch (Exception ex)
        {
            rule.LastRunResult = $"Exception: {ex.Message}";
            _logger.LogError(ex, "[DCA] Exception executing rule {Id}", ruleId);
        }

        rule.LastRunAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
