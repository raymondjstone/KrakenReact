using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

/// <summary>Which candles are finished, and how many days back a refresh looks to correct recent rows.</summary>
public static class KlineRules
{
    /// <summary>Daily refreshes start this many days before the newest stored candle, so a candle stored while it was still
    /// forming is fetched again once complete and corrected.</summary>
    public const int RecentRefetchDays = 3;

    /// <summary>The longest history Kraken serves in one OHLC request.</summary>
    public const int MaxHistoryCandles = 720;

    public static TimeSpan? Duration(string? interval) => interval switch
    {
        "OneMinute" => TimeSpan.FromMinutes(1),
        "ThreeMinutes" => TimeSpan.FromMinutes(3),
        "FiveMinutes" => TimeSpan.FromMinutes(5),
        "FifteenMinutes" => TimeSpan.FromMinutes(15),
        "ThirtyMinutes" => TimeSpan.FromMinutes(30),
        "OneHour" => TimeSpan.FromHours(1),
        "TwoHour" => TimeSpan.FromHours(2),
        "FourHour" => TimeSpan.FromHours(4),
        "SixHour" => TimeSpan.FromHours(6),
        "EightHour" => TimeSpan.FromHours(8),
        "TwelveHour" => TimeSpan.FromHours(12),
        "OneDay" => TimeSpan.FromDays(1),
        "ThreeDay" => TimeSpan.FromDays(3),
        "OneWeek" => TimeSpan.FromDays(7),
        "FifteenDays" => TimeSpan.FromDays(15),
        _ => null,
    };

    /// <summary>
    /// True once the candle's period is over. Kraken always ends an OHLC response with the current, still-forming candle; storing
    /// it freezes a half-built high, low, close and volume. An interval this code does not know is treated as finished, so an
    /// unfamiliar name never silently stops candles being saved.
    /// </summary>
    public static bool IsClosed(DerivedKline kline, DateTime utcNow) =>
        Duration(kline.Interval) is not { } length || kline.OpenTime + length <= utcNow;
}

public static class KlineStore
{
    /// <summary>
    /// Splits finished candles into those to insert and those whose stored copy is out of date. Unfinished candles are dropped,
    /// duplicates within the batch collapse to one, and a stored row is only touched when its values actually differ.
    /// </summary>
    public static (List<DerivedKline> ToAdd, List<(DerivedKline Stored, DerivedKline Fresh)> ToUpdate) Reconcile(
        IReadOnlyDictionary<string, DerivedKline> storedByKey, IEnumerable<DerivedKline> incoming, DateTime utcNow)
    {
        var toAdd = new List<DerivedKline>();
        var toUpdate = new List<(DerivedKline, DerivedKline)>();
        var seen = new HashSet<string>();
        foreach (var k in incoming)
        {
            if (!KlineRules.IsClosed(k, utcNow)) continue;
            k.Key = DerivedKline.MakeKey(k.Asset, k.Interval, k.OpenTime);
            if (!seen.Add(k.Key)) continue;

            if (!storedByKey.TryGetValue(k.Key, out var stored)) toAdd.Add(k);
            else if (Differs(stored, k)) toUpdate.Add((stored, k));
        }
        return (toAdd, toUpdate);
    }

    private static bool Differs(DerivedKline a, DerivedKline b) =>
        a.Open != b.Open || a.High != b.High || a.Low != b.Low || a.Close != b.Close || a.Volume != b.Volume ||
        a.VolumeWeightedAveragePrice != b.VolumeWeightedAveragePrice || a.TradeCount != b.TradeCount;

    public static void CopyValues(DerivedKline target, DerivedKline source)
    {
        target.Open = source.Open;
        target.High = source.High;
        target.Low = source.Low;
        target.Close = source.Close;
        target.Volume = source.Volume;
        target.VolumeWeightedAveragePrice = source.VolumeWeightedAveragePrice;
        target.TradeCount = source.TradeCount;
    }

    /// <summary>
    /// Saves finished candles, inserting new ones and correcting stored ones whose values differ, in small batches so no single
    /// transaction holds locks for long. Returns how many rows were inserted and updated.
    /// </summary>
    public static async Task<(int Added, int Updated)> UpsertAsync(
        IDbContextFactory<KrakenDbContext> factory, IEnumerable<DerivedKline> incoming, DateTime utcNow,
        int batchSize = 100, TimeSpan? pauseBetweenBatches = null, CancellationToken ct = default)
    {
        var closed = incoming.Where(k => KlineRules.IsClosed(k, utcNow)).ToList();
        int added = 0, updated = 0;

        for (var i = 0; i < closed.Count; i += batchSize)
        {
            var batch = closed.Skip(i).Take(batchSize).ToList();
            foreach (var k in batch) k.Key = DerivedKline.MakeKey(k.Asset, k.Interval, k.OpenTime);
            var keys = batch.Select(k => k.Key).Distinct().ToList();

            await using var db = await factory.CreateDbContextAsync(ct);
            var stored = await db.DerivedKlines.Where(k => keys.Contains(k.Key)).ToDictionaryAsync(k => k.Key, ct);
            var (toAdd, toUpdate) = Reconcile(stored, batch, utcNow);

            foreach (var (row, fresh) in toUpdate) CopyValues(row, fresh);
            db.DerivedKlines.AddRange(toAdd);
            if (toAdd.Count + toUpdate.Count == 0) continue;

            try { await db.SaveChangesAsync(ct); added += toAdd.Count; updated += toUpdate.Count; }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2627 or 2601 }) { }

            if (pauseBetweenBatches is { } pause && i + batchSize < closed.Count) await Task.Delay(pause, ct);
        }
        return (added, updated);
    }
}
