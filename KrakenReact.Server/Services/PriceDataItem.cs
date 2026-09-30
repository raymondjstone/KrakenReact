using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class PriceDataItem
{
    public string Symbol { get; set; } = "";
    public string SymbolNoSlash => TradingStateService.NormalizeAsset(Base) + TradingStateService.NormalizeAsset(CCY);
    public string SymbolNoSlashNoStaking => TradingStateService.NormalizeAsset(Base) + TradingStateService.NormalizeAsset(CCY);
    public string Base => (Symbol ?? "/").Split("/").FirstOrDefault() ?? "";
    public string CCY => (Symbol ?? "/").Split("/").LastOrDefault() ?? "";
    public bool SupportedPair { get; set; }
    public bool KrakenNewPricesLoadedEver { get; set; }
    public string KrakenNewPricesLoaded { get; set; } = "no";
    public DateTime KrakenNewPricesLoadedTime { get; set; } = DateTime.MinValue;
    private TickerDataItem? _tickerData;

    public TickerDataItem? TickerData
    {
        get => Volatile.Read(ref _tickerData);
        set => Volatile.Write(ref _tickerData, value);
    }

    /// <summary>The ticker data, creating it if absent. Both price feeds call this; two racing <c>??=</c> could each create one and
    /// one feed writes would land on the discarded copy.</summary>
    public TickerDataItem EnsureTickerData()
    {
        var existing = TickerData;
        if (existing != null) return existing;
        var created = new TickerDataItem();
        return Interlocked.CompareExchange(ref _tickerData, created, null) ?? created;
    }

    private readonly List<DerivedKline> _klineSnapshot = new(10000);
    private readonly object _klineLock = new();
    private const int MaxKlines = 10000;

    public string CoinType
    {
        get
        {
            var b = TradingStateService.NormalizeAsset(Base);
            if (TradingStateService.Blacklist.Contains(b)) return "Blacklist";
            if (TradingStateService.MajorCoin.Contains(b)) return "Main Coin";
            if (TradingStateService.Currency.Contains(b)) return "Currency";
            return "Minor Coin";
        }
    }

    public bool PriceOutdated
    {
        get
        {
            if (KrakenNewPricesLoaded == "loaded" && KrakenNewPricesLoadedTime > DateTime.UtcNow.AddMinutes(-20))
                return false;
            return !(SupportedPair && KrakenNewPricesLoadedEver);
        }
    }

    public void AddKline(DerivedKline kline)
    {
        if (kline == null) return;
        lock (_klineLock)
        {
            _klineSnapshot.Add(kline);
            if (_klineSnapshot.Count > MaxKlines)
                _klineSnapshot.RemoveRange(0, _klineSnapshot.Count - MaxKlines);
        }
        KrakenNewPricesLoadedTime = DateTime.UtcNow;
    }

    private DerivedKline? _liveKline;

    /// <summary>
    /// Records the latest live tick. Unlike <see cref="AddKline"/> this REPLACES the previous live tick rather
    /// than appending, so a busy pair can't fill the 10,000-slot list with one-minute ticks and evict its
    /// daily history (which broke Age, WeightedPrice and the auto-order "older than a year" check).
    /// </summary>
    public void SetLiveKline(DerivedKline kline)
    {
        if (kline == null) return;
        lock (_klineLock)
        {
            if (_liveKline != null)
            {
                // Usually last, but a history merge can sort a newer bar after it — search from the end.
                for (var i = _klineSnapshot.Count - 1; i >= 0 && i >= _klineSnapshot.Count - 5; i--)
                    if (ReferenceEquals(_klineSnapshot[i], _liveKline)) { _klineSnapshot.RemoveAt(i); break; }
            }
            _klineSnapshot.Add(kline);
            _liveKline = kline;
            if (_klineSnapshot.Count > MaxKlines)
                _klineSnapshot.RemoveRange(0, _klineSnapshot.Count - MaxKlines);
        }
        KrakenNewPricesLoadedTime = DateTime.UtcNow;
    }

    public void AddKlineHistory(List<DerivedKline> klines)
    {
        if (!klines.Any()) return;
        lock (_klineLock)
        {
            // Remove existing klines that overlap with new data (same OpenTime+Interval)
            var newDates = new HashSet<(DateTime, string)>(
                klines.Select(k => (k.OpenTime, k.Interval)));
            _klineSnapshot.RemoveAll(k => newDates.Contains((k.OpenTime, k.Interval)));

            _klineSnapshot.AddRange(klines);
            _klineSnapshot.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));

            if (_klineSnapshot.Count > MaxKlines)
                _klineSnapshot.RemoveRange(0, _klineSnapshot.Count - MaxKlines);
        }
    }

    public List<DerivedKline> GetKlineSnapshot()
    {
        lock (_klineLock) { return _klineSnapshot.ToList(); }
    }

    public DerivedKline? LatestKline
    {
        get { lock (_klineLock) { return _klineSnapshot.LastOrDefault(); } }
    }

    /// <summary>Returns the best available price — prefers live ticker (always current) over kline snapshot (may be stale daily close).</summary>
    public DerivedKline? BestKline
    {
        get
        {
            // Live ticker is updated on every V1 WebSocket tick; prefer it to avoid stale daily-kline closes
            var tickerPrice = TickerData?.LastTradePrice ?? 0;
            if (tickerPrice > 0)
                return new DerivedKline { Asset = TradingStateService.NormalizeAsset(Base), Close = tickerPrice, OpenTime = DateTime.UtcNow };
            var k = LatestKline;
            if (k != null) return k;
            return null;
        }
    }

    public DerivedKline? MinKline
    {
        get { lock (_klineLock) { return _klineSnapshot.FirstOrDefault(l => l.OpenTime.Year > 1967); } }
    }

    public string Age => AgeFrom(null);

    /// <summary>As <see cref="Age"/>, from an already-taken kline snapshot (avoids copying the list again).</summary>
    public string AgeFrom(List<DerivedKline>? snapshot)
    {
        {
            var min = snapshot != null ? snapshot.FirstOrDefault(l => l.OpenTime.Year > 1967) : MinKline;
            if (min == null) return "Unknown";
            var t = DateTime.UtcNow - min.OpenTime; // OpenTime is UTC everywhere else; DateTime.Now skewed the age by the UTC offset
            if (t.TotalDays > 36500) return "Unknown";
            if (t.TotalDays > 365) return "Old";
            if (t.TotalDays > 180) return "SixMonths";
            if (t.TotalDays > 90) return "ThreeMonths";
            if (t.TotalDays > 28) return "OneMonth";
            if (t.TotalDays > 13) return "TwoWeeks";
            if (t.TotalDays > 6) return "OneWeek";
            if (t.TotalDays > 2) return "FewDays";
            return "New";
        }
    }

    // The kline-derived figures below each used to take their own copy of the whole kline list (under its lock). Building a
    // price row needs about twenty of them, so the overloads accept one snapshot that the caller takes once.

    public decimal? ClosePriceDiff(int days, List<DerivedKline>? snapshot = null)
    {
        var k = snapshot ?? GetKlineSnapshot();
        if (k.Count < 2) return null;
        var last = k.LastOrDefault(l => l != null);
        if (last == null || last.OpenTime <= DateTime.MinValue) return null;
        var dayoldlist = k.Where(a => a.OpenTime > last.OpenTime.AddDays(-1 * days)).ToList();
        return last.Close - (dayoldlist.Any() ? dayoldlist.First().Close : last.Close);
    }

    public decimal CloseMovementDiff(int days, List<DerivedKline>? snapshot = null)
    {
        var x = ClosePriceDiff(days, snapshot);
        if (x == null) return 0m;
        var close = (snapshot != null ? snapshot.LastOrDefault() : LatestKline)?.Close ?? 0m;
        if (close == 0m) return 0m;
        return Math.Round((x.Value / (close / 100m)), 6);
    }

    public decimal? ClosePriceAverage(int days, List<DerivedKline>? snapshot = null)
    {
        var k = snapshot ?? GetKlineSnapshot();
        if (k.Count < 2) return null;
        var last = k.LastOrDefault(a => a.OpenTime > DateTime.MinValue);
        if (last == null) return null;
        var dayoldlist = k.Where(a => a.OpenTime > last.OpenTime.AddDays(-1 * days)).ToList();
        if (dayoldlist.Count <= 2) return null;
        return Math.Round(dayoldlist.Sum(a => a.Close) / dayoldlist.Count, 6);
    }

    public decimal? WeightedPrice => WeightedPriceFrom(null);

    public decimal? WeightedPriceFrom(List<DerivedKline>? snapshot)
    {
        {
            decimal total = 0m; int weight = 0;
            var avgDay = ClosePriceAverage(1, snapshot);
            var avgWeek = ClosePriceAverage(7, snapshot);
            var avgMonth = ClosePriceAverage(31, snapshot);
            var avgYear = ClosePriceAverage(365, snapshot);
            if (avgDay.HasValue) { total += avgDay.Value * 6; weight += 6; }
            if (avgWeek.HasValue) { total += avgWeek.Value * 4; weight += 4; }
            if (avgMonth.HasValue) { total += avgMonth.Value * 2; weight += 2; }
            if (avgYear.HasValue) { total += avgYear.Value * 1; weight += 1; }
            if (weight == 0) return null;
            return Math.Round(total / weight, 6);
        }
    }

    public decimal? WeightedPricePercentage => WeightedPricePercentageFrom(null);

    public decimal? WeightedPricePercentageFrom(List<DerivedKline>? snapshot)
    {
        {
            var close = (snapshot != null ? snapshot.LastOrDefault() : LatestKline)?.Close;
            var wp = WeightedPriceFrom(snapshot);
            if (!KrakenNewPricesLoadedEver || close == null || wp == null || close <= 0 || wp < 0) return null;
            if (AgeFrom(snapshot) != "Old") return 200.0m;
            return Math.Round((wp.Value / close.Value) * 100, 2);
        }
    }
}

/// <summary>
/// The live quote for one pair, shared between the price feeds (writers) and every job that trades or alerts on it (readers).
/// <para>
/// A <c>decimal</c> is 16 bytes and .NET does not guarantee that writing one is atomic, so a reader racing the feed can see half
/// of the old price and half of the new - a number that never existed - and a stop-loss or auto-order could act on it. The data
/// is therefore held as two IMMUTABLE snapshots, each swapped in by a single reference write (which is atomic): the exchange
/// quote (written by the V1 feed) and the 24h statistics (written by the V2 feed). Readers always see one complete, consistent
/// version of each. The property names are unchanged, so existing code reads and writes as before.
/// </para>
/// </summary>
public class TickerDataItem
{
    /// <summary>One complete version of the quote. Replaced as a whole, never edited.</summary>
    public sealed record QuoteSnapshot(
        decimal BestAsk = 0, decimal BestBid = 0, decimal Last = 0, decimal Open = 0, decimal High = 0,
        decimal Low = 0, decimal Volume = 0, decimal Vwap = 0, int TradeCount = 0);

    /// <summary>One complete version of the rolling 24h statistics. Replaced as a whole, never edited.</summary>
    public sealed record StatsSnapshot(decimal? Change24h = null, decimal? ChangePct24h = null);

    private QuoteSnapshot _quote = new();
    private StatsSnapshot _stats = new();

    public QuoteSnapshot Quote => Volatile.Read(ref _quote);
    public StatsSnapshot Stats => Volatile.Read(ref _stats);

    /// <summary>Publishes a whole new quote at once (what the live feed uses: one write per tick, never a half-updated quote).</summary>
    public void SetQuote(QuoteSnapshot quote) => Volatile.Write(ref _quote, quote);

    /// <summary>Publishes new 24h statistics at once.</summary>
    public void SetStats(StatsSnapshot stats) => Volatile.Write(ref _stats, stats);

    // Per-field access, kept so existing callers (and object initializers) work unchanged. Each setter swaps in a new snapshot
    // through a compare-and-swap, so concurrent setters of different fields cannot lose each other's update.
    private void UpdateQuote(Func<QuoteSnapshot, QuoteSnapshot> change)
    {
        QuoteSnapshot current, next;
        do { current = Volatile.Read(ref _quote); next = change(current); }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _quote, next, current), current));
    }

    private void UpdateStats(Func<StatsSnapshot, StatsSnapshot> change)
    {
        StatsSnapshot current, next;
        do { current = Volatile.Read(ref _stats); next = change(current); }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _stats, next, current), current));
    }

    public decimal BestAskPrice { get => Quote.BestAsk; set => UpdateQuote(q => q with { BestAsk = value }); }
    public decimal BestBidPrice { get => Quote.BestBid; set => UpdateQuote(q => q with { BestBid = value }); }
    public decimal LastTradePrice { get => Quote.Last; set => UpdateQuote(q => q with { Last = value }); }
    public decimal OpenPrice { get => Quote.Open; set => UpdateQuote(q => q with { Open = value }); }
    public decimal HighPrice { get => Quote.High; set => UpdateQuote(q => q with { High = value }); }
    public decimal LowPrice { get => Quote.Low; set => UpdateQuote(q => q with { Low = value }); }
    public decimal Volume { get => Quote.Volume; set => UpdateQuote(q => q with { Volume = value }); }
    public decimal VolumeWeightedAvgPrice { get => Quote.Vwap; set => UpdateQuote(q => q with { Vwap = value }); }
    public int TradeCount { get => Quote.TradeCount; set => UpdateQuote(q => q with { TradeCount = value }); }
    public decimal? Change24h { get => Stats.Change24h; set => UpdateStats(s => s with { Change24h = value }); }
    public decimal? ChangePct24h { get => Stats.ChangePct24h; set => UpdateStats(s => s with { ChangePct24h = value }); }
}
