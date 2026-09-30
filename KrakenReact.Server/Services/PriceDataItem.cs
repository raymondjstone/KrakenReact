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
    public TickerDataItem? TickerData { get; set; }

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

    public string Age
    {
        get
        {
            var min = MinKline;
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

    public decimal? ClosePriceDiff(int days)
    {
        var k = GetKlineSnapshot();
        if (k.Count < 2) return null;
        var last = k.LastOrDefault(l => l != null);
        if (last == null || last.OpenTime <= DateTime.MinValue) return null;
        var dayoldlist = k.Where(a => a.OpenTime > last.OpenTime.AddDays(-1 * days)).ToList();
        return last.Close - (dayoldlist.Any() ? dayoldlist.First().Close : last.Close);
    }

    public decimal CloseMovementDiff(int days)
    {
        var x = ClosePriceDiff(days);
        if (x == null) return 0m;
        var close = LatestKline?.Close ?? 0m;
        if (close == 0m) return 0m;
        return Math.Round((x.Value / (close / 100m)), 6);
    }

    public decimal? ClosePriceAverage(int days)
    {
        var k = GetKlineSnapshot();
        if (k.Count < 2) return null;
        var last = k.LastOrDefault(a => a.OpenTime > DateTime.MinValue);
        if (last == null) return null;
        var dayoldlist = k.Where(a => a.OpenTime > last.OpenTime.AddDays(-1 * days)).ToList();
        if (dayoldlist.Count <= 2) return null;
        return Math.Round(dayoldlist.Sum(a => a.Close) / dayoldlist.Count, 6);
    }

    public decimal? WeightedPrice
    {
        get
        {
            decimal total = 0m; int weight = 0;
            var avgDay = ClosePriceAverage(1);
            var avgWeek = ClosePriceAverage(7);
            var avgMonth = ClosePriceAverage(31);
            var avgYear = ClosePriceAverage(365);
            if (avgDay.HasValue) { total += avgDay.Value * 6; weight += 6; }
            if (avgWeek.HasValue) { total += avgWeek.Value * 4; weight += 4; }
            if (avgMonth.HasValue) { total += avgMonth.Value * 2; weight += 2; }
            if (avgYear.HasValue) { total += avgYear.Value * 1; weight += 1; }
            if (weight == 0) return null;
            return Math.Round(total / weight, 6);
        }
    }

    public decimal? WeightedPricePercentage
    {
        get
        {
            var close = LatestKline?.Close;
            var wp = WeightedPrice;
            if (!KrakenNewPricesLoadedEver || close == null || wp == null || close <= 0 || wp < 0) return null;
            if (Age != "Old") return 200.0m;
            return Math.Round((wp.Value / close.Value) * 100, 2);
        }
    }
}

public class TickerDataItem
{
    public decimal BestAskPrice { get; set; }
    public decimal BestBidPrice { get; set; }
    public decimal LastTradePrice { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal Volume { get; set; }
    public decimal VolumeWeightedAvgPrice { get; set; }
    public int TradeCount { get; set; }
    public decimal? Change24h { get; set; }
    public decimal? ChangePct24h { get; set; }
}
