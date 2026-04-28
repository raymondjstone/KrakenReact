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
    private readonly Dictionary<string, Dictionary<DateTime, MarketContextPoint>> _marketContextCache = new(StringComparer.OrdinalIgnoreCase);

    // Minimum labelled rows needed to train (after warmup)
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
        _kraken = kraken;
        _state = state;
        _dbFactory = dbFactory;
        _hub = hub;
        _logger = logger;
    }

    public async Task ExecuteSingleAsync(string symbol, CancellationToken ct = default)
    {
        var interval = GetInterval();
        var intervalStr = interval.ToString();
        _logger.LogInformation("[Predict] Single-symbol job starting - {Symbol} @ {Interval}", symbol, intervalStr);
        try
        {
            var result = await ProcessSymbolAsync(symbol, interval, intervalStr, ct);
            await UpsertResultAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Predict] Error processing {Symbol}", symbol);
        }
        try { await _hub.Clients.All.SendAsync("PredictionsUpdated", ct); }
        catch { /* non-critical */ }
    }

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var symbols = await GetSymbolsAsync(ct);
        var interval = GetInterval();
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

            // Brief pause between symbols to avoid hammering the API
            try { await Task.Delay(600, ct); } catch (OperationCanceledException) { break; }
        }

        try { await _hub.Clients.All.SendAsync("PredictionsUpdated", ct); }
        catch { /* non-critical */ }

        _logger.LogInformation("[Predict] Job complete");
    }

    // Per-symbol pipeline

    private async Task<PredictionResult> ProcessSymbolAsync(
        string symbol, KlineInterval interval, string intervalStr, CancellationToken ct)
    {
        await FetchAndStoreKlinesAsync(symbol, interval, intervalStr, ct);
        var marketContext = await GetMarketContextAsync(interval, intervalStr, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var klines = await db.DerivedKlines
            .Where(k => k.Asset == symbol && k.Interval == intervalStr)
            .AsNoTracking()
            .OrderBy(k => k.OpenTime)
            .ToListAsync(ct);

        int maxHorizon = PredictionHorizons.Max();
        int requiredCandles = FeatureEngineering.Warmup + MinTrainRows + maxHorizon;
        if (klines.Count < requiredCandles)
        {
            _logger.LogWarning("[Predict] {Symbol}: only {N} candles (need {Min})", symbol, klines.Count, requiredCandles);
            return MakeResult(symbol, intervalStr, "insufficient_data", klines.Count, 0, 0,
                $"Only {klines.Count} candles stored. Need {requiredCandles}+. " +
                $"Initial refreshes backfill history automatically, but this market/interval may not have enough data yet.");
        }

        var h1 = EvaluateHorizon(symbol, klines, marketContext, 1);
        var h3 = EvaluateHorizon(symbol, klines, marketContext, 3);
        var h6 = EvaluateHorizon(symbol, klines, marketContext, 6);

        if (h1.Status != "success")
        {
            return MakeResult(symbol, intervalStr, h1.Status, klines.Count, h1.TrainSamples, h1.TestSamples, h1.ErrorMessage);
        }

        _logger.LogInformation(
            "[Predict] {Symbol}: 1c {Dir1} ({Prob1:P0}) | 3c {Dir3} ({Prob3:P0}) | 6c {Dir6} ({Prob6:P0}) | 1c acc={Acc:P1} auc={Auc:F3} wf={WfAcc:P1}/{WfAuc:F3}",
            symbol,
            h1.PredictedUp ? "UP" : "DOWN", h1.Probability,
            h3.PredictedUp ? "UP" : "DOWN", h3.Probability,
            h6.PredictedUp ? "UP" : "DOWN", h6.Probability,
            h1.ModelAccuracy, h1.ModelAuc, h1.WalkForwardAccuracy, h1.WalkForwardAuc);

        return new PredictionResult
        {
            Symbol = symbol,
            Interval = intervalStr,
            ComputedAt = DateTime.UtcNow,
            Status = "success",
            PredictedUp = h1.PredictedUp,
            Probability = h1.Probability,
            ModelAccuracy = h1.ModelAccuracy,
            ModelAuc = h1.ModelAuc,
            WalkForwardAccuracy = h1.WalkForwardAccuracy,
            WalkForwardAuc = h1.WalkForwardAuc,
            WalkForwardFoldCount = h1.WalkForwardFoldCount,
            LogRegAccuracy = h1.LogRegAccuracy,
            BenchmarkBuyHold = h1.BenchmarkBuyHold,
            BenchmarkSma = h1.BenchmarkSma,
            PredictedUp3 = h3.PredictedUp,
            Probability3 = h3.Probability,
            ModelAccuracy3 = h3.ModelAccuracy,
            ModelAuc3 = h3.ModelAuc,
            WalkForwardAccuracy3 = h3.WalkForwardAccuracy,
            WalkForwardAuc3 = h3.WalkForwardAuc,
            WalkForwardFoldCount3 = h3.WalkForwardFoldCount,
            LogRegAccuracy3 = h3.LogRegAccuracy,
            BenchmarkBuyHold3 = h3.BenchmarkBuyHold,
            BenchmarkSma3 = h3.BenchmarkSma,
            TrainSamples3 = h3.TrainSamples,
            TestSamples3 = h3.TestSamples,
            PredictedUp6 = h6.PredictedUp,
            Probability6 = h6.Probability,
            ModelAccuracy6 = h6.ModelAccuracy,
            ModelAuc6 = h6.ModelAuc,
            WalkForwardAccuracy6 = h6.WalkForwardAccuracy,
            WalkForwardAuc6 = h6.WalkForwardAuc,
            WalkForwardFoldCount6 = h6.WalkForwardFoldCount,
            LogRegAccuracy6 = h6.LogRegAccuracy,
            BenchmarkBuyHold6 = h6.BenchmarkBuyHold,
            BenchmarkSma6 = h6.BenchmarkSma,
            TrainSamples6 = h6.TrainSamples,
            TestSamples6 = h6.TestSamples,
            TrainSamples = h1.TrainSamples,
            TestSamples = h1.TestSamples,
            TotalCandles = klines.Count,
        };
    }

    private HorizonEvaluation EvaluateHorizon(
        string symbol,
        List<DerivedKline> klines,
        Dictionary<DateTime, MarketContextPoint> marketContext,
        int horizon)
    {
        var features = FeatureEngineering.BuildFeatures(klines, horizon, marketContext);
        if (features.Rows.Count < MinTrainRows)
        {
            return HorizonEvaluation.Error(horizon, "insufficient_data", $"Only {features.Rows.Count} valid feature rows after warmup.");
        }
        if (features.LatestFeatures == null)
        {
            return HorizonEvaluation.Error(horizon, "error", "Could not compute latest feature row.");
        }

        int splitIdx = (int)(features.Rows.Count * 0.70);
        var trainRows = features.Rows.Take(splitIdx).ToList();
        var testRows = features.Rows.Skip(splitIdx).ToList();
        if (trainRows.Count < MinTrainRows || testRows.Count == 0)
        {
            return HorizonEvaluation.Error(horizon, "insufficient_data", "Not enough train/test samples after chronological split.");
        }

        var mlContext = new MLContext(seed: 42 + horizon);
        var (ffAcc, ffAuc, ffModel) = TrainFastTree(mlContext, trainRows, testRows);
        var (lrAcc, _) = TrainLogisticRegression(mlContext, trainRows, testRows);
        var (wfAcc, wfAuc, wfFolds) = EvaluateWalkForward(mlContext, features.Rows);

        var engine = mlContext.Model.CreatePredictionEngine<CandleFeatures, BinaryPrediction>(ffModel);
        var next = engine.Predict(features.LatestFeatures);

        float buyHold = (float)testRows.Count(r => r.Label) / testRows.Count;
        float smaBench = ComputeSmaBenchmark(features, splitIdx, testRows.Count, horizon);

        return new HorizonEvaluation
        {
            Horizon = horizon,
            Status = "success",
            PredictedUp = next.PredictedLabel,
            Probability = next.Probability,
            ModelAccuracy = ffAcc,
            ModelAuc = ffAuc,
            WalkForwardAccuracy = wfAcc,
            WalkForwardAuc = wfAuc,
            WalkForwardFoldCount = wfFolds,
            LogRegAccuracy = lrAcc,
            BenchmarkBuyHold = buyHold,
            BenchmarkSma = smaBench,
            TrainSamples = trainRows.Count,
            TestSamples = testRows.Count,
        };
    }

    // Data fetching

    private async Task FetchAndStoreKlinesAsync(string symbol, KlineInterval interval, string intervalStr, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var query = db.DerivedKlines
                .Where(k => k.Asset == symbol && k.Interval == intervalStr)
                .AsNoTracking();

            var existingCount = await query.CountAsync(ct);
            var lastStored = await query
                .MaxAsync(k => (DateTime?)k.OpenTime, ct);

            // Symbols with no or too little history should bootstrap from a larger
            // interval-aware lookback instead of only appending from the latest candle.
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
        KlineInterval interval,
        string intervalStr,
        CancellationToken ct)
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

    // ML training

    private static DateTime GetBootstrapSinceUtc(KlineInterval interval)
    {
        var span = interval switch
        {
            KlineInterval.OneMinute => TimeSpan.FromMinutes(1),
            KlineInterval.FiveMinutes => TimeSpan.FromMinutes(5),
            KlineInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
            KlineInterval.ThirtyMinutes => TimeSpan.FromMinutes(30),
            KlineInterval.FourHour => TimeSpan.FromHours(4),
            KlineInterval.OneDay => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1),
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

        var model = pipeline.Fit(ml.Data.LoadFromEnumerable(trainRows));
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

            var model = pipeline.Fit(ml.Data.LoadFromEnumerable(trainRows));
            var metrics = ml.BinaryClassification.Evaluate(
                model.Transform(ml.Data.LoadFromEnumerable(testRows)));

            return (SafeMetric(metrics.Accuracy), SafeMetric(metrics.AreaUnderRocCurve));
        }
        catch
        {
            return (0f, 0f);
        }
    }

    private static (float Accuracy, float Auc, int FoldCount) EvaluateWalkForward(
        MLContext ml, List<CandleFeatures> rows)
    {
        if (rows.Count < MinTrainRows + WalkForwardFolds)
            return (0f, 0f, 0);

        int initialTrainSize = Math.Max(MinTrainRows, (int)(rows.Count * 0.5f));
        if (initialTrainSize >= rows.Count)
            return (0f, 0f, 0);

        int remainingRows = rows.Count - initialTrainSize;
        int targetFolds = Math.Min(WalkForwardFolds, remainingRows);
        if (targetFolds <= 0)
            return (0f, 0f, 0);

        int foldSize = Math.Max(1, remainingRows / targetFolds);
        float weightedAccuracy = 0f;
        float weightedAuc = 0f;
        int weightedSamples = 0;
        int foldsUsed = 0;

        for (int fold = 0; fold < targetFolds; fold++)
        {
            int testStart = initialTrainSize + fold * foldSize;
            if (testStart >= rows.Count)
                break;

            int testCount = fold == targetFolds - 1
                ? rows.Count - testStart
                : Math.Min(foldSize, rows.Count - testStart);

            if (testCount <= 0)
                break;

            var foldTrainRows = rows.Take(testStart).ToList();
            var foldTestRows = rows.Skip(testStart).Take(testCount).ToList();
            if (foldTrainRows.Count < MinTrainRows || foldTestRows.Count == 0)
                continue;

            var (acc, auc, _) = TrainFastTree(ml, foldTrainRows, foldTestRows);
            weightedAccuracy += acc * foldTestRows.Count;
            weightedAuc += auc * foldTestRows.Count;
            weightedSamples += foldTestRows.Count;
            foldsUsed++;
        }

        if (weightedSamples == 0 || foldsUsed == 0)
            return (0f, 0f, 0);

        return (weightedAccuracy / weightedSamples, weightedAuc / weightedSamples, foldsUsed);
    }

    private static float SafeMetric(double value) => double.IsFinite(value) ? (float)value : 0f;

    // Benchmarks

    private static float ComputeSmaBenchmark(FeatureSet fs, int splitIdx, int testCount, int horizon)
    {
        // SMA 5/20 crossover: predict UP when SMA5 > SMA20, else DOWN
        // Compare against actual label for the test window rows
        if (testCount == 0 || fs.Sma5.Length == 0 || fs.Sma20.Length == 0) return 0.5f;

        int correct = 0;
        int offset = FeatureEngineering.Warmup + splitIdx;
        for (int i = 0; i < testCount && offset + i < fs.Sma5.Length; i++)
        {
            bool signal = fs.Sma5[offset + i] > fs.Sma20[offset + i];
            bool actual = fs.Closes.Length > offset + i + horizon && fs.Closes[offset + i + horizon] > fs.Closes[offset + i];
            if (signal == actual) correct++;
        }
        return testCount > 0 ? (float)correct / testCount : 0.5f;
    }

    // DB persistence

    private async Task UpsertResultAsync(PredictionResult result)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.PredictionResults.FindAsync(result.Symbol);
        if (existing == null)
            db.PredictionResults.Add(result);
        else
        {
            existing.Interval = result.Interval;
            existing.ComputedAt = result.ComputedAt;
            existing.Status = result.Status;
            existing.PredictedUp = result.PredictedUp;
            existing.Probability = result.Probability;
            existing.ModelAccuracy = result.ModelAccuracy;
            existing.ModelAuc = result.ModelAuc;
            existing.WalkForwardAccuracy = result.WalkForwardAccuracy;
            existing.WalkForwardAuc = result.WalkForwardAuc;
            existing.WalkForwardFoldCount = result.WalkForwardFoldCount;
            existing.LogRegAccuracy = result.LogRegAccuracy;
            existing.BenchmarkBuyHold = result.BenchmarkBuyHold;
            existing.BenchmarkSma = result.BenchmarkSma;
            existing.PredictedUp3 = result.PredictedUp3;
            existing.Probability3 = result.Probability3;
            existing.ModelAccuracy3 = result.ModelAccuracy3;
            existing.ModelAuc3 = result.ModelAuc3;
            existing.WalkForwardAccuracy3 = result.WalkForwardAccuracy3;
            existing.WalkForwardAuc3 = result.WalkForwardAuc3;
            existing.WalkForwardFoldCount3 = result.WalkForwardFoldCount3;
            existing.LogRegAccuracy3 = result.LogRegAccuracy3;
            existing.BenchmarkBuyHold3 = result.BenchmarkBuyHold3;
            existing.BenchmarkSma3 = result.BenchmarkSma3;
            existing.TrainSamples3 = result.TrainSamples3;
            existing.TestSamples3 = result.TestSamples3;
            existing.PredictedUp6 = result.PredictedUp6;
            existing.Probability6 = result.Probability6;
            existing.ModelAccuracy6 = result.ModelAccuracy6;
            existing.ModelAuc6 = result.ModelAuc6;
            existing.WalkForwardAccuracy6 = result.WalkForwardAccuracy6;
            existing.WalkForwardAuc6 = result.WalkForwardAuc6;
            existing.WalkForwardFoldCount6 = result.WalkForwardFoldCount6;
            existing.LogRegAccuracy6 = result.LogRegAccuracy6;
            existing.BenchmarkBuyHold6 = result.BenchmarkBuyHold6;
            existing.BenchmarkSma6 = result.BenchmarkSma6;
            existing.TrainSamples6 = result.TrainSamples6;
            existing.TestSamples6 = result.TestSamples6;
            existing.TrainSamples = result.TrainSamples;
            existing.TestSamples = result.TestSamples;
            existing.TotalCandles = result.TotalCandles;
            existing.ErrorMessage = result.ErrorMessage;
        }
        await db.SaveChangesAsync();
    }

    // Settings helpers

    private async Task<List<string>> GetSymbolsAsync(CancellationToken ct = default)
    {
        if (_state.PredictionMode == "all")
        {
            var currency = (_state.PredictionCurrency ?? "USD").Trim();
            var suffix = $"/{currency}";
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
        if (string.IsNullOrWhiteSpace(raw))
            raw = "XBT/USD,ETH/USD,SOL/USD";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0)
                  .Distinct()
                  .ToList();
    }

    private KlineInterval GetInterval()
    {
        return _state.PredictionInterval switch
        {
            "OneMinute" => KlineInterval.OneMinute,
            "FiveMinutes" => KlineInterval.FiveMinutes,
            "FifteenMinutes" => KlineInterval.FifteenMinutes,
            "ThirtyMinutes" => KlineInterval.ThirtyMinutes,
            "FourHour" => KlineInterval.FourHour,
            "OneDay" => KlineInterval.OneDay,
            _ => KlineInterval.OneHour,
        };
    }

    private static PredictionResult MakeResult(string symbol, string interval, string status,
        int totalCandles, int train, int test, string? error = null) => new()
    {
        Symbol = symbol,
        Interval = interval,
        ComputedAt = DateTime.UtcNow,
        Status = status,
        TotalCandles = totalCandles,
        TrainSamples = train,
        TestSamples = test,
        ErrorMessage = error,
    };

    private sealed class HorizonEvaluation
    {
        public int Horizon { get; init; }
        public string Status { get; init; } = "success";
        public bool PredictedUp { get; init; }
        public float Probability { get; init; }
        public float ModelAccuracy { get; init; }
        public float ModelAuc { get; init; }
        public float WalkForwardAccuracy { get; init; }
        public float WalkForwardAuc { get; init; }
        public int WalkForwardFoldCount { get; init; }
        public float LogRegAccuracy { get; init; }
        public float BenchmarkBuyHold { get; init; }
        public float BenchmarkSma { get; init; }
        public int TrainSamples { get; init; }
        public int TestSamples { get; init; }
        public string? ErrorMessage { get; init; }

        public static HorizonEvaluation Error(int horizon, string status, string message) => new()
        {
            Horizon = horizon,
            Status = status,
            ErrorMessage = message,
        };
    }
}
