using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

/// <summary>
/// Collects and retains one-minute candles for every pair the account actually trades.
/// <para>
/// Minute history cannot be bought back later. Kraken's OHLC endpoint returns at most 720 bars,
/// which at one-minute resolution is twelve hours — so a minute series only exists if something was
/// recording it at the time. That is the whole reason this job exists: it starts the clock, and the
/// analysis that needs minute resolution becomes possible some months from now rather than never.
/// </para>
/// <para>
/// The pair set is recomputed every run from the trade history, so a pair traded for the first time
/// today is picked up on the next pass without anyone configuring anything.
/// </para>
/// </summary>
public class MinuteCandleJob
{
    private const string IntervalName = "OneMinute";

    /// <summary>Kraken returns at most 720 OHLC bars, so a run can never recover more than this.</summary>
    private const int MaxBarsPerFetch = 720;

    /// <summary>Pause between pairs, to stay well inside the public rate limit.</summary>
    private const int PairDelayMs = 600;

    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly SqlTimeoutDiagnostics _sqlDiag;
    private readonly ILogger<MinuteCandleJob> _logger;

    /// <summary>Stops two overlapping runs from fetching and inserting the same bars.</summary>
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    /// <summary>
    /// When the retention sweep last ran. The delete filters on Interval and OpenTime, and no index
    /// leads with either, so it scans the whole kline table — cheap once a day, wasteful every ten
    /// minutes when there is at most one day's worth of newly expired rows to find.
    /// </summary>
    private static DateTime _lastPruneUtc = DateTime.MinValue;

    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(20);

    public MinuteCandleJob(
        IDbContextFactory<KrakenDbContext> dbFactory,
        KrakenRestService kraken,
        TradingStateService state,
        SqlTimeoutDiagnostics sqlDiag,
        ILogger<MinuteCandleJob> logger)
    {
        _dbFactory = dbFactory;
        _kraken = kraken;
        _state = state;
        _sqlDiag = sqlDiag;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!await RunLock.WaitAsync(0, ct))
        {
            _logger.LogInformation("[Minute] A previous run is still going; skipping this one");
            return;
        }

        try
        {
            var pairs = await GetTrackedPairsAsync(ct);
            if (pairs.Count == 0)
            {
                _logger.LogInformation("[Minute] No traded pairs found to collect minute candles for");
                return;
            }

            _logger.LogInformation("[Minute] Collecting one-minute candles for {Count} pairs", pairs.Count);

            int totalStored = 0, pairsWithGaps = 0;
            foreach (var pair in pairs)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var (stored, hadGap) = await CollectPairAsync(pair, ct);
                    totalStored += stored;
                    if (hadGap) pairsWithGaps++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Minute] {Pair}: collection failed", pair);
                    _sqlDiag.CaptureIfTimeout($"MinuteCandleJob({pair})", ex);
                }

                try { await Task.Delay(PairDelayMs, ct); } catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("[Minute] Stored {Stored} new candles across {Pairs} pairs{Gaps}",
                totalStored, pairs.Count,
                pairsWithGaps > 0 ? $"; {pairsWithGaps} pair(s) had an unrecoverable gap" : "");

            if (DateTime.UtcNow - _lastPruneUtc >= PruneEvery)
            {
                _lastPruneUtc = DateTime.UtcNow;
                await PruneAsync(ct);
            }
        }
        finally
        {
            RunLock.Release();
        }
    }

    /// <summary>
    /// The pairs to collect for: everything traded within the lookback window. Recomputed each run so
    /// newly traded pairs join automatically and long-abandoned ones drop out.
    /// </summary>
    public async Task<List<string>> GetTrackedPairsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        int months = await ReadIntSettingAsync(db, "MinuteCandleLookbackMonths", 5, 1, 120, ct);
        var since = DateTime.UtcNow.AddMonths(-months);

        var symbols = await db.Trades
            .Where(t => t.Timestamp >= since && t.Symbol != null)
            .Select(t => t.Symbol!)
            .Distinct()
            .ToListAsync(ct);

        // Trade symbols are Kraken's REST pair names (XXBTZUSD); the kline store is keyed by the
        // websocket name (XBT/USD), which is what every other part of the app reads by.
        return symbols
            .Select(ToWebsocketName)
            .Where(p => !string.IsNullOrEmpty(p) && !TradingStateService.BadPairs.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ToWebsocketName(string tradeSymbol)
    {
        if (tradeSymbol.Contains('/')) return tradeSymbol;
        var baseAsset = _state.NormalizeOrderSymbolBase(tradeSymbol);
        var quoteAsset = _state.NormalizeOrderSymbolQuote(tradeSymbol);
        return string.IsNullOrEmpty(baseAsset) || string.IsNullOrEmpty(quoteAsset) ? "" : $"{baseAsset}/{quoteAsset}";
    }

    /// <summary>Fetches and stores whatever minute bars are new for one pair.</summary>
    private async Task<(int Stored, bool HadGap)> CollectPairAsync(string pair, CancellationToken ct)
    {
        // Probe first and release the context before the network call, so a connection is not held
        // open across HTTP to Kraken.
        DateTime? lastStored;
        await using (var probeDb = await _dbFactory.CreateDbContextAsync(ct))
        {
            lastStored = await probeDb.DerivedKlines
                .Where(k => k.Asset == pair && k.Interval == IntervalName)
                .AsNoTracking()
                .MaxAsync(k => (DateTime?)k.OpenTime, ct);
        }

        // Asking for more than the endpoint will return only wastes the window; on a first run, or
        // after an outage longer than the reach, take the most recent 720 bars and accept the gap.
        var reach = DateTime.UtcNow.AddMinutes(-MaxBarsPerFetch);
        bool hadGap = lastStored is not null && lastStored < reach;
        var since = lastStored is null || hadGap ? reach : lastStored;

        if (hadGap)
            _logger.LogWarning(
                "[Minute] {Pair}: last stored candle was {Last:u}, beyond the {Bars}-bar reach — that gap cannot be recovered",
                pair, lastStored, MaxBarsPerFetch);

        var apiKlines = await _kraken.GetKlinesAsync(pair, KlineInterval.OneMinute, since);
        var candidates = apiKlines
            .Select(k => new DerivedKline(k, pair, KlineInterval.OneMinute))
            .ToList();
        if (candidates.Count == 0) return (0, hadGap);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var earliest = candidates.Min(k => k.OpenTime);
        var existingKeys = await db.DerivedKlines
            .Where(k => k.Asset == pair && k.Interval == IntervalName && k.OpenTime >= earliest)
            .Select(k => k.Key)
            .ToHashSetAsync(ct);

        // The most recent bar is still forming, so its high, low and close will change. Storing it
        // now would freeze a half-built candle that the next run would then skip as already present.
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var toAdd = candidates
            .Where(k => k.OpenTime < cutoff && !existingKeys.Contains(k.Key))
            .ToList();
        if (toAdd.Count == 0) return (0, hadGap);

        const int batchSize = 500;
        for (int i = 0; i < toAdd.Count; i += batchSize)
        {
            db.DerivedKlines.AddRange(toAdd.Skip(i).Take(batchSize));
            await db.SaveChangesAsync(ct);
        }

        return (toAdd.Count, hadGap);
    }

    /// <summary>
    /// Drops minute candles past the retention window.
    /// <para>
    /// Minute data is bulky: one pair produces 1,440 rows a day, so forty pairs add about half a
    /// million rows a week. Without a bound this table would grow until it became the largest thing
    /// in the database. Deleting in batches keeps the transaction short, which matters on a server
    /// that already sees flush waits.
    /// </para>
    /// </summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        int retentionDays;
        await using (var settingsDb = await _dbFactory.CreateDbContextAsync(ct))
            retentionDays = await ReadIntSettingAsync(settingsDb, "MinuteCandleRetentionDays", 400, 7, 3650, ct);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        int totalDeleted = 0;
        const int batchLimit = 20;
        for (int batch = 0; batch < batchLimit && !ct.IsCancellationRequested; batch++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            int deleted = await db.DerivedKlines
                .Where(k => k.Interval == IntervalName && k.OpenTime < cutoff)
                .Take(5000)
                .ExecuteDeleteAsync(ct);

            totalDeleted += deleted;
            if (deleted == 0) break;
        }

        if (totalDeleted > 0)
            _logger.LogInformation("[Minute] Pruned {Count} candles older than {Days} days", totalDeleted, retentionDays);
    }

    /// <summary>Reads an integer setting, clamped, falling back to the default when absent or unparseable.</summary>
    private static async Task<int> ReadIntSettingAsync(KrakenDbContext db, string key, int fallback, int min, int max, CancellationToken ct)
    {
        var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting is not null && int.TryParse(setting.Value, out int parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    /// <summary>How much minute history each tracked pair has, for the UI to report progress.</summary>
    public async Task<List<MinuteCoverage>> GetCoverageAsync(CancellationToken ct)
    {
        var pairs = await GetTrackedPairsAsync(ct);
        if (pairs.Count == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stats = await db.DerivedKlines
            .Where(k => k.Interval == IntervalName && pairs.Contains(k.Asset))
            .GroupBy(k => k.Asset)
            .Select(g => new
            {
                Asset = g.Key,
                Count = g.Count(),
                First = g.Min(k => k.OpenTime),
                Last = g.Max(k => k.OpenTime),
            })
            .ToListAsync(ct);

        var byAsset = stats.ToDictionary(s => s.Asset, StringComparer.OrdinalIgnoreCase);
        return pairs
            .Select(p => byAsset.TryGetValue(p, out var s)
                ? new MinuteCoverage(p, s.Count, s.First, s.Last, (decimal)Math.Round((s.Last - s.First).TotalDays, 1))
                : new MinuteCoverage(p, 0, null, null, 0m))
            .OrderByDescending(c => c.CandleCount)
            .ToList();
    }
}

/// <summary>How much minute history one pair has accumulated so far.</summary>
public sealed record MinuteCoverage(
    string Pair,
    int CandleCount,
    DateTime? FirstOpenTime,
    DateTime? LastOpenTime,
    decimal SpanDays);
