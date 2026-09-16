using System.Collections.Concurrent;
using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;

namespace KrakenReact.Server.Services;

/// <summary>
/// Computes % price change over several rolling windows (1h/4h/6h/12h/24h) for a pair.
/// <para>
/// 24h comes straight from Kraken's live ticker (change_pct) — the one window Kraken gives us as a
/// true rolling figure, so it is used as-is with no fallback. The others have no ticker equivalent,
/// so they're approximated from a single shared batch of hourly klines: current live price vs. the
/// close of the hourly candle closest to (but not after) N hours ago — the same kind of approximation
/// the app already uses for its week/month figures (see PriceDataItem.CloseMovementDiff).
/// </para>
/// <para>
/// The hourly klines are cached briefly per symbol so frequent callers — the MicroTrade page polling
/// every 15s, several rules sharing a pair, the MicroTrade job itself — don't each trigger their own
/// Kraken REST call.
/// </para>
/// </summary>
public class PriceChangeService
{
    public static readonly int[] SupportedHours = { 1, 4, 6, 12, 24 };

    private static readonly TimeSpan KlineCacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (DateTime CachedAt, List<KrakenKline> Klines)> _klineCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly ILogger<PriceChangeService> _logger;

    public PriceChangeService(KrakenRestService kraken, TradingStateService state, ILogger<PriceChangeService> logger)
    {
        _kraken = kraken;
        _state = state;
        _logger = logger;
    }

    /// <summary>Returns % change for every supported window, keyed by hours. Null where data isn't available yet.</summary>
    public async Task<Dictionary<int, decimal?>> GetChangesAsync(string symbol, PriceDataItem? priceItem = null)
    {
        priceItem ??= ResolvePriceItem(symbol);
        var result = new Dictionary<int, decimal?> { [24] = priceItem?.TickerData?.ChangePct24h };

        var currentPrice = priceItem?.BestKline?.Close ?? 0m;
        if (currentPrice <= 0)
        {
            foreach (var h in SupportedHours.Where(h => h != 24)) result[h] = null;
            return result;
        }

        var klines = await GetHourlyKlinesAsync(symbol);
        foreach (var h in SupportedHours.Where(h => h != 24))
            result[h] = ComputeChange(currentPrice, klines, h);

        return result;
    }

    /// <summary>Returns % change for a single window. Prefer this over GetChangesAsync when only one is needed.</summary>
    public async Task<decimal?> GetChangeAsync(string symbol, int hours, PriceDataItem? priceItem = null)
    {
        priceItem ??= ResolvePriceItem(symbol);
        if (hours == 24) return priceItem?.TickerData?.ChangePct24h;

        var currentPrice = priceItem?.BestKline?.Close ?? 0m;
        if (currentPrice <= 0) return null;

        var klines = await GetHourlyKlinesAsync(symbol);
        return ComputeChange(currentPrice, klines, hours);
    }

    private static decimal? ComputeChange(decimal currentPrice, List<KrakenKline> klines, int hours)
    {
        var target = DateTime.UtcNow.AddHours(-hours);
        var reference = klines
            .Where(k => k.OpenTime <= target)
            .OrderByDescending(k => k.OpenTime)
            .FirstOrDefault();
        if (reference == null || reference.ClosePrice <= 0) return null;

        return Math.Round((currentPrice - reference.ClosePrice) / reference.ClosePrice * 100m, 4);
    }

    private async Task<List<KrakenKline>> GetHourlyKlinesAsync(string symbol)
    {
        if (_klineCache.TryGetValue(symbol, out var cached) && DateTime.UtcNow - cached.CachedAt < KlineCacheTtl)
            return cached.Klines;

        List<KrakenKline> klines;
        try
        {
            // Widest supported window is 12h; pad by a couple of hours so the candle at-or-before
            // the target is always present even with a slow-forming latest bar.
            var since = DateTime.UtcNow.AddHours(-14);
            klines = (await _kraken.GetKlinesAsync(symbol, KlineInterval.OneHour, since)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PriceChange] Failed to fetch hourly klines for {Symbol}", symbol);
            return new List<KrakenKline>();
        }

        _klineCache[symbol] = (DateTime.UtcNow, klines);
        return klines;
    }

    private PriceDataItem? ResolvePriceItem(string symbol)
    {
        var key = _state.ResolveSymbolKey(symbol);
        _state.Prices.TryGetValue(key, out var priceItem);
        if (priceItem?.TickerData?.ChangePct24h != null) return priceItem;

        var parts = symbol.Split('/');
        if (parts.Length == 2)
        {
            var normBase = TradingStateService.NormalizeAsset(parts[0]);
            var normCcy = TradingStateService.NormalizeAsset(parts[1]);
            var sibling = _state.Prices.Values.FirstOrDefault(p =>
                p.TickerData?.ChangePct24h != null &&
                TradingStateService.NormalizeAsset(p.Base) == normBase &&
                TradingStateService.NormalizeAsset(p.CCY) == normCcy);
            if (sibling != null) return sibling;
        }

        return priceItem;
    }
}
