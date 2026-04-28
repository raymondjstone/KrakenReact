using System.Collections.Concurrent;
using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.Hubs;
using KrakenReact.Server.Models;
using Kraken.Net.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;

namespace KrakenReact.Server.Services;

public class PredictionJob
{
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<PredictionJob> _logger;

    // Per-run cache: interval string → market context map.
    // Instance-level is fine because this service is transient (one instance per job invocation).
    private readonly Dictionary<string, Dictionary<DateTime, MarketContextPoint>> _marketContextCache
        = new(StringComparer.OrdinalIgnoreCase);

    // Guards against concurrent runs of ExecuteSingleAsync for the same symbol.
    // Static so it persists across transient instances.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _symbolLocks
        = new(StringComparer.OrdinalIgnoreCase);

    private const int MinTrainRows = 80;
    private const int WalkForwardFolds = 4;
    private const int SeedHistoryCandles = 240;
    private const string MarketContextSymbol = "XBT/USD";
    private static readonly int[] PredictionHorizons = [1, 3, 6];

    public PredictionJob(
        KrakenRestService kraken,
        TradingStateService state,
        IDbContextFactory<KrakenDbContext> dbFactory,
        IHubContext<TradingHub> hub,
        ILogger<PredictionJob> logger)
    {
        _kraken   = kraken;
        _state    = state;
        _dbFactory = dbFactory;
        _hub      = hub;
        _logger   = logger;
    }

    public async Task ExecuteSingleAsync(string symbol, CancellationToken ct = default)
    {
        // Drop duplicate concurrent refreshes for the same symbol rather than queuing them
        var sem = _symbolLocks.GetOrAdd(symbol, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct))
        {
            _logger.LogInformation("[Predict] Single-symbol job skipped (already running) - {Symbol}", symbol);
            return;
        }

        try
        {
            var interval    = GetInterval();
            var intervalStr = interval.ToString();
            _logger.LogInformation("[Predict] Single-symbol job starting - {Symbol} @ {Interval}", symbol, intervalStr);

            var result = await ProcessSymbolAsync(symbol, interval, intervalStr, ct);
            await UpsertResultAsync(result);

            try { await _hub.Clients.All.SendAsync("PredictionsUpdated", symbol, ct); }
            catch { /* non-critical */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Predict] Error processing {Symbol}", symbol);
        }
        finally
        {
            sem.Release();
        }
    }

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var symbols     = await GetSymbolsAsync(ct);
        var interval    = GetInterval();
        var intervalStr = interval.ToString();

        _logger.LogInformation("[Predict] Job starting - {Count} symbols @ {Interval}", symbols.Count, intervalStr);

        foreach (var symbol in symbols)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var result = await ProcessSymbolAsync(symbol, interval, intervalStr, ct);
                await UpsertResultAsync(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Predict] Error processing {Symbol}", symbol);
            }

            try { await Task.Delay(600, ct); } catch (OperationCanceledException) { break; }
        }

        try { await _hub.Clients.All.SendAsync("PredictionsUpdated", (string?)null, ct); }
        catch { /* non-critical */ }

        _logger.LogInformation("[Predict] Job complete");
    }

    // ── Per-symbol pipeline ─────────────────────────────────────────────────

    private async Task<PredictionResult> ProcessSymbolAsync(
        string symbol, KlineInterval interval, string intervalStr, CancellationToken ct)
    {
        await FetchAndStoreKlinesAsync(symbol, interval, intervalStr, ct);
        var marketContext = await GetMarketContextAsync(interval, intervalStr, ct);

        // When predicting the BTC/USD benchmark itself, its own returns are the target variable —
        // zeroing out the market context features prevents self-referential data leakage.
        bool isBtcSelf = symbol.Equals(MarketContextSymbol, StringComparison.OrdinalIgnoreCase);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var klines = await db.DerivedKlines
            .Where(k => k.Asset == symbol && k.Interval == intervalStr)
            .AsNoTracking()
            .OrderBy(k => k.OpenTime)
            .ToListAsync(ct);

        int maxHorizon       = PredictionHorizons.Max();
        int requiredCandles  = FeatureEngineering.Warmup + MinTrainRows + maxHorizon;
        if (klines.Count < requiredCandles)
        {
            _logger.LogWarning("[Predict] {Symbol}: only {N} candles (need {Min})", symbol, klines.Count, requiredCandles);
            return MakeResult(symbol, intervalStr, "insufficient_data", klines.Count, 0, 0,
                $"Only {klines.Count} candles stored. Need {requiredCandles}+. " +
                "Initial refreshes backfill history automatically, but this market/interval may not have enough data yet.");
        }

        // Build indicators once and share across all three horizon evaluations
        var indicators = FeatureEngineering.BuildIndicators(klines);

        var h1 = EvaluateHorizon(indicators, marketContext, 1, isBtcSelf);
        var h3 = EvaluateHorizon(indicators, marketContext, 3, isBtcSelf);
        var h6 = EvaluateHorizon(indicators, marketContext, 6, isBtcSelf);

        if (h1.Status != "success")
            return MakeResult(symbol, intervalStr, h1.Status, klines.Count, h1.TrainSamples, h1.TestSamples, h1.ErrorMessage);

        _logger.LogInformation(
            "[Predict] {Symbol}: 1c {Dir1} ({Prob1:P0}) | 3c {Dir3} ({Prob3:P0}) | 6c {Dir6} ({Prob6:P0}) | 1c acc={Acc:P1} auc={Auc:F3} wf={WfAcc:P1}/{WfAuc:F3}",
            symbol,
            h1.PredictedUp ? "UP" : "DOWN", h1.Probability,
            h3.PredictedUp ? "UP" : "DOWN", h3.Probability,
            h6.PredictedUp ? "UP" : "DOWN", h6.Probability,
            h1.ModelAccuracy, h1.ModelAuc, h1.WalkForwardAccuracy, h1.WalkForwardAuc);

        return new PredictionResult
        {
            Symbol    = symbol,
            Interval  = intervalStr,
            ComputedAt = DateTime.UtcNow,
            Status    = "success",

            PredictedUp              = h1.PredictedUp,
            Probability              = h1.Probability,
            ModelAccuracy            = h1.ModelAccuracy,
            ModelAuc                 = h1.ModelAuc,
            WalkForwardAccuracy      = h1.WalkForwardAccuracy,
            WalkForwardAuc           = h1.WalkForwardAuc,
            WalkForwardFoldCount     = h1.WalkForwardFoldCount,
            WalkForwardLogRegAccuracy = h1.WalkForwardLogRegAccuracy,
            WalkForwardLogRegAuc     = h1.WalkForwardLogRegAuc,
            LogRegAccuracy           = h1.LogRegAccuracy,
            BenchmarkBuyHold         = h1.BenchmarkBuyHold,
            BenchmarkSma             = h1.BenchmarkSma,
            TrainSamples             = h1.TrainSamples,
            TestSamples              = h1.TestSamples,

            PredictedUp3              = h3.PredictedUp,
            Probability3              = h3.Probability,
            ModelAccuracy3            = h3.ModelAccuracy,
            ModelAuc3                 = h3.ModelAuc,
            WalkForwardAccuracy3      = h3.WalkForwardAccuracy,
            WalkForwardAuc3           = h3.WalkForwardAuc,
            WalkForwardFoldCount3     = h3.WalkForwardFoldCount,
            WalkForwardLogRegAccuracy3 = h3.WalkForwardLogRegAccuracy,
            WalkForwardLogRegAuc3     = h3.WalkForwardLogRegAuc,
            LogRegAccuracy3           = h3.LogRegAccuracy,
            BenchmarkBuyHold3         = h3.BenchmarkBuyHold,
            BenchmarkSma3             = h3.BenchmarkSma,
            TrainSamples3             = h3.TrainSamples,
            TestSamples3              = h3.TestSamples,

            PredictedUp6              = h6.PredictedUp,
            Probability6              = h6.Probability,
            ModelAccuracy6            = h6.ModelAccuracy,
            ModelAuc6                 = h6.ModelAuc,
            WalkForwardAccuracy6      = h6.WalkForwardAccuracy,
            WalkForwardAuc6           = h6.WalkForwardAuc,
            WalkForwardFoldCount6     = h6.WalkForwardFoldCount,
            WalkForwardLogRegAccuracy6 = h6.WalkForwardLogRegAccuracy,
            WalkForwardLogRegAuc6     = h6.WalkForwardLogRegAuc,
            LogRegAccuracy6           = h6.LogRegAccuracy,
            BenchmarkBuyHold6         = h6.BenchmarkBuyHold,
            BenchmarkSma6             = h6.BenchmarkSma,
            TrainSamples6             = h6.TrainSamples,
            TestSamples6              = h6.TestSamples,

            TotalCandles = klines.Count,
        };
    }

    private HorizonEvaluation EvaluateHorizon(
        ComputedIndicators indicators,
        Dictionary<DateTime, MarketContextPoint> marketContext,
        int horizon,
        bool zeroMarketContext = false)
    {
        var features = FeatureEngineering.BuildFeatures(indicators, horizon, marketContext, zeroMarketContext);
        if (features.Rows.Count < MinTrainRows)
            return HorizonEvaluation.Error(horizon, "insufficient_data", $"Only {features.Rows.Count} valid feature rows after warmup.");
        if (features.LatestFeatures == null)
            return HorizonEvaluation.Error(horizon, "error", "Could not compute latest feature row.");

        int splitIdx  = (int)(features.Rows.Count * 0.70);
        var trainRows = features.Rows.Take(splitIdx).ToList();
        var testRows  = features.Rows.Skip(splitIdx).ToList();
        if (trainRows.Count < MinTrainRows || testRows.Count == 0)
            return HorizonEvaluation.Error(horizon, "insufficient_data", "Not enough train/test samples after chronological split.");

        var mlContext = new MLContext(seed: 42 + horizon);
        var (ffAcc, ffAuc, ffModel) = TrainFastTree(mlContext, trainRows, testRows);
        var (lrAcc, _)              = TrainLogisticRegression(mlContext, trainRows, testRows);
        var (wfFtAcc, wfFtAuc, wfLrAcc, wfLrAuc, wfFolds) = EvaluateWalkForward(mlContext, features.Rows);

        var engine = mlContext.Model.CreatePredictionEngine<CandleFeatures, BinaryPrediction>(ffModel);
        var next   = engine.Predict(features.LatestFeatures);

        float buyHold  = (float)testRows.Count(r => r.Label) / testRows.Count;
        float smaBench = ComputeSmaBenchmark(features, splitIdx, testRows.Count, horizon);

        return new HorizonEvaluation
        {
            Horizon                  = horizon,
            Status                   = "success",
            PredictedUp              = next.PredictedLabel,
            Probability              = next.Probability,
            ModelAccuracy            = ffAcc,
            ModelAuc                 = ffAuc,
            WalkForwardAccuracy      = wfFtAcc,
            WalkForwardAuc           = wfFtAuc,
            WalkForwardFoldCount     = wfFolds,
            WalkForwardLogRegAccuracy = wfLrAcc,
            WalkForwardLogRegAuc     = wfLrAuc,
            LogRegAccuracy           = lrAcc,
            BenchmarkBuyHold         = buyHold,
            BenchmarkSma             = smaBench,
            TrainSamples             = trainRows.Count,
            TestSamples              = testRows.Count,
        };
    }

    // ── Data fetching ───────────────────────────────────────────────────────

    private async Task FetchAndStoreKlinesAsync(string symbol, KlineInterval interval, string intervalStr, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var query = db.DerivedKlines
                .Where(k => k.Asset == symbol && k.Interval == intervalStr)
                .AsNoTracking();

            var existingCount = await query.CountAsync(ct);
            var lastStored    = await query.MaxAsync(k => (DateTime?)k.OpenTime, ct);

            var since = existingCount < SeedHistoryCandles
                ? GetBootstrapSinceUtc(interval)
                : lastStored;
            var apiKlines = await _kraken.GetKlinesAsync(symbol, interval, since);

            var newDerived = apiKlines
                .Select(k => new DerivedKline(k, symbol, interval))
                .ToList();

            if (newDerived.Count == 0) return;

            var existingKeys = await db.DerivedKlines
                .Where(k => k.Asset == symbol && k.Interval == intervalStr)
                .Select(k => k.Key)
                .ToHashSetAsync(ct);

            var toAdd = newDerived.Where(k => !existingKeys.Contains(k.Key)).ToList();
            if (toAdd.Count > 0)
            {
                db.DerivedKlines.AddRange(toAdd);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("[Predict] {Symbol}: stored {N} new {Interval} candles", symbol, toAdd.Count, intervalStr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Predict] {Symbol}: kline fetch failed (will use existing data)", symbol);
        }
    }

    private async Task<Dictionary<DateTime, MarketContextPoint>> GetMarketContextAsync(
        KlineInterval interval, string intervalStr, CancellationToken ct)
    {
        if (_marketContextCache.TryGetValue(intervalStr, out var cached))
            return cached;

        await FetchAndStoreKlinesAsync(MarketContextSymbol, interval, intervalStr, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var klines = await db.DerivedKlines
            .Where(k => k.Asset == MarketContextSymbol && k.Interval == intervalStr)
            .AsNoTracking()
            .OrderBy(k => k.OpenTime)
            .ToListAsync(ct);

        var map = FeatureEngineering.BuildMarketContextMap(klines);
        _marketContextCache[intervalStr] = map;
        return map;
    }

    // ── ML training ─────────────────────────────────────────────────────────

    private static DateTime GetBootstrapSinceUtc(KlineInterval interval)
    {
        var span = interval switch
        {
            KlineInterval.OneMinute      => TimeSpan.FromMinutes(1),
            KlineInterval.FiveMinutes    => TimeSpan.FromMinutes(5),
            KlineInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
            KlineInterval.ThirtyMinutes  => TimeSpan.FromMinutes(30),
            KlineInterval.FourHour       => TimeSpan.FromHours(4),
            KlineInterval.OneDay         => TimeSpan.FromDays(1),
            _                            => TimeSpan.FromHours(1),
        };
        return DateTime.UtcNow - TimeSpan.FromTicks(span.Ticks * SeedHistoryCandles);
    }

    private static (float Accuracy, float Auc, ITransformer Model) TrainFastTree(
        MLContext ml, List<CandleFeatures> trainRows, List<CandleFeatures> testRows)
    {
        var pipeline = ml.Transforms.Concatenate("Features", FeatureEngineering.FeatureColumnNames)
            .Append(ml.Transforms.NormalizeMinMax("Features"))
            .Append(ml.BinaryClassification.Trainers.FastTree(
                numberOfLeaves: 24,
                numberOfTrees: 120,
                minimumExampleCountPerLeaf: 5,
                learningRate: 0.08));

        var model   = pipeline.Fit(ml.Data.LoadFromEnumerable(trainRows));
        var metrics = ml.BinaryClassification.Evaluate(
            model.Transform(ml.Data.LoadFromEnumerable(testRows)));

        return (SafeMetric(metrics.Accuracy), SafeMetric(metrics.AreaUnderRocCurve), model);
    }

    private static (float Accuracy, float Auc) TrainLogisticRegression(
        MLContext ml, List<CandleFeatures> trainRows, List<CandleFeatures> testRows)
    {
        try
        {
            var pipeline = ml.Transforms.Concatenate("Features", FeatureEngineering.FeatureColumnNames)
                .Append(ml.Transforms.NormalizeMinMax("Features"))
                .Append(ml.BinaryClassification.Trainers.LbfgsLogisticRegression());

            var model   = pipeline.Fit(ml.Data.LoadFromEnumerable(trainRows));
            var metrics = ml.BinaryClassification.Evaluate(
                model.Transform(ml.Data.LoadFromEnumerable(testRows)));

            return (SafeMetric(metrics.Accuracy), SafeMetric(metrics.AreaUnderRocCurve));
        }
        catch
        {
            return (0f, 0f);
        }
    }

    /// <summary>
    /// Walk-forward validation: trains both FastTree and Logistic Regression on expanding windows
    /// and returns weighted-average accuracy and AUC for each model across all folds.
    /// </summary>
    private static (float FtAccuracy, float FtAuc, float LrAccuracy, float LrAuc, int FoldCount)
        EvaluateWalkForward(MLContext ml, List<CandleFeatures> rows)
    {
        if (rows.Count < MinTrainRows + WalkForwardFolds)
            return (0f, 0f, 0f, 0f, 0);

        int initialTrainSize = Math.Max(MinTrainRows, (int)(rows.Count * 0.5f));
        if (initialTrainSize >= rows.Count)
            return (0f, 0f, 0f, 0f, 0);

        int remainingRows = rows.Count - initialTrainSize;
        int targetFolds   = Math.Min(WalkForwardFolds, remainingRows);
        if (targetFolds <= 0)
            return (0f, 0f, 0f, 0f, 0);

        int foldSize = Math.Max(1, remainingRows / targetFolds);

        float ftAccSum = 0f, ftAucSum = 0f;
        float lrAccSum = 0f, lrAucSum = 0f;
        int   weightedSamples = 0, foldsUsed = 0;

        for (int fold = 0; fold < targetFolds; fold++)
        {
            int testStart = initialTrainSize + fold * foldSize;
            if (testStart >= rows.Count) break;

            int testCount = fold == targetFolds - 1
                ? rows.Count - testStart
                : Math.Min(foldSize, rows.Count - testStart);
            if (testCount <= 0) break;

            var foldTrain = rows.Take(testStart).ToList();
            var foldTest  = rows.Skip(testStart).Take(testCount).ToList();
            if (foldTrain.Count < MinTrainRows || foldTest.Count == 0) continue;

            var (ftAcc, ftAuc, _) = TrainFastTree(ml, foldTrain, foldTest);
            var (lrAcc, lrAuc)    = TrainLogisticRegression(ml, foldTrain, foldTest);

            ftAccSum += ftAcc * foldTest.Count;
            ftAucSum += ftAuc * foldTest.Count;
            lrAccSum += lrAcc * foldTest.Count;
            lrAucSum += lrAuc * foldTest.Count;
            weightedSamples += foldTest.Count;
            foldsUsed++;
        }

        if (weightedSamples == 0 || foldsUsed == 0)
            return (0f, 0f, 0f, 0f, 0);

        return (
            ftAccSum / weightedSamples,
            ftAucSum / weightedSamples,
            lrAccSum / weightedSamples,
            lrAucSum / weightedSamples,
            foldsUsed);
    }

    private static float SafeMetric(double value) => double.IsFinite(value) ? (float)value : 0f;

    // ── Benchmarks ──────────────────────────────────────────────────────────

    private static float ComputeSmaBenchmark(FeatureSet fs, int splitIdx, int testCount, int horizon)
    {
        if (testCount == 0 || fs.Sma5.Length == 0 || fs.Sma20.Length == 0) return 0.5f;

        int correct = 0;
        int offset  = FeatureEngineering.Warmup + splitIdx;
        for (int i = 0; i < testCount && offset + i < fs.Sma5.Length; i++)
        {
            bool signal = fs.Sma5[offset + i] > fs.Sma20[offset + i];
            bool actual = fs.Closes.Length > offset + i + horizon
                && fs.Closes[offset + i + horizon] > fs.Closes[offset + i];
            if (signal == actual) correct++;
        }
        return testCount > 0 ? (float)correct / testCount : 0.5f;
    }

    // ── DB persistence ──────────────────────────────────────────────────────

    private async Task UpsertResultAsync(PredictionResult result)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.PredictionResults.FindAsync(result.Symbol);
        if (existing == null)
        {
            db.PredictionResults.Add(result);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(result);
        }

        // Append to history (only on success so we track meaningful trend data)
        if (result.Status == "success")
        {
            db.PredictionHistories.Add(new Models.PredictionHistory
            {
                Symbol              = result.Symbol,
                ComputedAt          = result.ComputedAt,
                PredictedUp         = result.PredictedUp,
                Probability         = result.Probability,
                ModelAccuracy       = result.ModelAccuracy,
                WalkForwardAccuracy = result.WalkForwardAccuracy,
                Interval            = result.Interval,
            });

            // Keep at most 90 history rows per symbol (rolling window)
            var count = await db.PredictionHistories.CountAsync(h => h.Symbol == result.Symbol);
            if (count > 90)
            {
                var oldest = await db.PredictionHistories
                    .Where(h => h.Symbol == result.Symbol)
                    .OrderBy(h => h.ComputedAt)
                    .Take(count - 90)
                    .ToListAsync();
                db.PredictionHistories.RemoveRange(oldest);
            }
        }

        await db.SaveChangesAsync();
    }

    // ── Settings helpers ────────────────────────────────────────────────────

    private async Task<List<string>> GetSymbolsAsync(CancellationToken ct = default)
    {
        if (_state.PredictionMode == "all")
        {
            var currency = (_state.PredictionCurrency ?? "USD").Trim();
            var suffix   = $"/{currency}";
            return _state.Symbols.Values
                .Where(s => s.WebsocketName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.WebsocketName)
                .OrderBy(s => s)
                .ToList();
        }

        if (_state.PredictionMode == "existing")
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.PredictionResults
                .AsNoTracking()
                .Select(r => r.Symbol)
                .OrderBy(r => r)
                .ToListAsync(ct);
        }

        var raw = _state.PredictionSymbols;
        if (string.IsNullOrWhiteSpace(raw)) raw = "XBT/USD,ETH/USD,SOL/USD";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0)
                  .Distinct()
                  .ToList();
    }

    private KlineInterval GetInterval() => _state.PredictionInterval switch
    {
        "OneMinute"      => KlineInterval.OneMinute,
        "FiveMinutes"    => KlineInterval.FiveMinutes,
        "FifteenMinutes" => KlineInterval.FifteenMinutes,
        "ThirtyMinutes"  => KlineInterval.ThirtyMinutes,
        "FourHour"       => KlineInterval.FourHour,
        "OneDay"         => KlineInterval.OneDay,
        _                => KlineInterval.OneHour,
    };

    private static PredictionResult MakeResult(string symbol, string interval, string status,
        int totalCandles, int train, int test, string? error = null) => new()
    {
        Symbol        = symbol,
        Interval      = interval,
        ComputedAt    = DateTime.UtcNow,
        Status        = status,
        TotalCandles  = totalCandles,
        TrainSamples  = train,
        TestSamples   = test,
        ErrorMessage  = error,
    };

    private sealed class HorizonEvaluation
    {
        public int    Horizon                  { get; init; }
        public string Status                   { get; init; } = "success";
        public bool   PredictedUp              { get; init; }
        public float  Probability              { get; init; }
        public float  ModelAccuracy            { get; init; }
        public float  ModelAuc                 { get; init; }
        public float  WalkForwardAccuracy      { get; init; }
        public float  WalkForwardAuc           { get; init; }
        public int    WalkForwardFoldCount     { get; init; }
        public float  WalkForwardLogRegAccuracy { get; init; }
        public float  WalkForwardLogRegAuc     { get; init; }
        public float  LogRegAccuracy           { get; init; }
        public float  BenchmarkBuyHold         { get; init; }
        public float  BenchmarkSma             { get; init; }
        public int    TrainSamples             { get; init; }
        public int    TestSamples              { get; init; }
        public string? ErrorMessage            { get; init; }

        public static HorizonEvaluation Error(int horizon, string status, string message) => new()
        {
            Horizon      = horizon,
            Status       = status,
            ErrorMessage = message,
        };
    }
}
