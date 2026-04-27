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

    // Minimum labelled rows needed to train (after warmup)
    private const int MinTrainRows = 80;

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

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var symbols  = GetSymbols();
        var interval = GetInterval();
        var intervalStr = interval.ToString();

        _logger.LogInformation("[Predict] Job starting — {Count} symbols @ {Interval}", symbols.Count, intervalStr);

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

    // ── Per-symbol pipeline ───────────────────────────────────────────────────

    private async Task<PredictionResult> ProcessSymbolAsync(
        string symbol, KlineInterval interval, string intervalStr, CancellationToken ct)
    {
        // 1. Fetch fresh klines from API and store any new ones
        await FetchAndStoreKlinesAsync(symbol, interval, intervalStr, ct);

        // 2. Load all stored klines for this symbol + interval
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var klines = await db.DerivedKlines
            .Where(k => k.Asset == symbol && k.Interval == intervalStr)
            .AsNoTracking()
            .OrderBy(k => k.OpenTime)
            .ToListAsync(ct);

        if (klines.Count < FeatureEngineering.Warmup + MinTrainRows + 1)
        {
            _logger.LogWarning("[Predict] {Symbol}: only {N} candles (need {Min})", symbol, klines.Count, FeatureEngineering.Warmup + MinTrainRows + 1);
            return MakeResult(symbol, intervalStr, "insufficient_data", klines.Count, 0, 0,
                $"Only {klines.Count} candles stored. Need {FeatureEngineering.Warmup + MinTrainRows + 1}+. " +
                $"Run the job a few more times to build up history.");
        }

        // 3. Build features
        var features = FeatureEngineering.BuildFeatures(klines);
        if (features.Rows.Count < MinTrainRows)
        {
            return MakeResult(symbol, intervalStr, "insufficient_data", klines.Count, 0, 0,
                $"Only {features.Rows.Count} valid feature rows after warmup.");
        }
        if (features.LatestFeatures == null)
        {
            return MakeResult(symbol, intervalStr, "error", klines.Count, 0, 0, "Could not compute latest feature row.");
        }

        // 4. Walk-forward split — chronological, no leakage
        int splitIdx  = (int)(features.Rows.Count * 0.70);
        var trainRows = features.Rows.Take(splitIdx).ToList();
        var testRows  = features.Rows.Skip(splitIdx).ToList();

        // 5. Train and evaluate both models
        var mlContext = new MLContext(seed: 42);

        var (ffAcc, ffAuc, ffModel) = TrainFastTree(mlContext, trainRows, testRows);
        var (lrAcc, _)             = TrainLogisticRegression(mlContext, trainRows, testRows);

        // 6. Predict next period using the gradient-boosted model
        var engine = mlContext.Model.CreatePredictionEngine<CandleFeatures, BinaryPrediction>(ffModel);
        var next   = engine.Predict(features.LatestFeatures);

        // 7. Compute benchmarks on the test window
        float buyHold = testRows.Count > 0 ? (float)testRows.Count(r => r.Label) / testRows.Count : 0.5f;
        float smaBench = ComputeSmaBenchmark(features, splitIdx, testRows.Count);

        _logger.LogInformation(
            "[Predict] {Symbol}: acc={Acc:P1} auc={Auc:F3} vs buy&hold={BH:P1} vs SMA={SMA:P1} | predict {Dir} ({Prob:P0})",
            symbol, ffAcc, ffAuc, buyHold, smaBench,
            next.PredictedLabel ? "UP" : "DOWN", next.Probability);

        return new PredictionResult
        {
            Symbol           = symbol,
            Interval         = intervalStr,
            ComputedAt       = DateTime.UtcNow,
            Status           = "success",
            PredictedUp      = next.PredictedLabel,
            Probability      = next.Probability,
            ModelAccuracy    = ffAcc,
            ModelAuc         = ffAuc,
            LogRegAccuracy   = lrAcc,
            BenchmarkBuyHold = buyHold,
            BenchmarkSma     = smaBench,
            TrainSamples     = trainRows.Count,
            TestSamples      = testRows.Count,
            TotalCandles     = klines.Count,
        };
    }

    // ── Data fetching ─────────────────────────────────────────────────────────

    private async Task FetchAndStoreKlinesAsync(string symbol, KlineInterval interval, string intervalStr, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var lastStored = await db.DerivedKlines
                .Where(k => k.Asset == symbol && k.Interval == intervalStr)
                .AsNoTracking()
                .MaxAsync(k => (DateTime?)k.OpenTime, ct);

            // Fetch from last stored time or 91 days back (enough for ~3 API pages of hourly data)
            var since = lastStored ?? DateTime.UtcNow.AddDays(-91);
            var apiKlines = await _kraken.GetKlinesAsync(symbol, interval, since);

            var newDerived = apiKlines
                .Select(k => new DerivedKline(k, symbol, interval))
                .ToList();

            if (newDerived.Count == 0) return;

            // Upsert: skip any that already exist
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

    // ── ML training ───────────────────────────────────────────────────────────

    private static (float Accuracy, float Auc, ITransformer Model) TrainFastTree(
        MLContext ml, List<CandleFeatures> trainRows, List<CandleFeatures> testRows)
    {
        var pipeline = ml.Transforms.Concatenate("Features", FeatureEngineering.FeatureColumnNames)
            .Append(ml.Transforms.NormalizeMinMax("Features"))
            .Append(ml.BinaryClassification.Trainers.FastTree(
                numberOfLeaves: 20,
                numberOfTrees: 100,
                minimumExampleCountPerLeaf: 5,
                learningRate: 0.1));

        var model   = pipeline.Fit(ml.Data.LoadFromEnumerable(trainRows));
        var metrics = ml.BinaryClassification.Evaluate(
            model.Transform(ml.Data.LoadFromEnumerable(testRows)));

        return ((float)metrics.Accuracy, (float)metrics.AreaUnderRocCurve, model);
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

            return ((float)metrics.Accuracy, (float)metrics.AreaUnderRocCurve);
        }
        catch
        {
            return (0f, 0f);
        }
    }

    // ── Benchmarks ────────────────────────────────────────────────────────────

    private static float ComputeSmaBenchmark(FeatureSet fs, int splitIdx, int testCount)
    {
        // SMA 5/20 crossover: predict UP when SMA5 > SMA20, else DOWN
        // Compare against actual label for the test window rows
        if (testCount == 0 || fs.Sma5.Length == 0 || fs.Sma20.Length == 0) return 0.5f;

        // The feature rows start at index FeatureEngineering.Warmup in the kline array
        // Row i in features.Rows corresponds to kline index Warmup + i
        int correct = 0;
        int offset  = FeatureEngineering.Warmup + splitIdx; // kline index for first test row
        for (int i = 0; i < testCount && offset + i < fs.Sma5.Length; i++)
        {
            bool signal = fs.Sma5[offset + i] > fs.Sma20[offset + i];
            bool actual = fs.Closes.Length > offset + i + 1 && fs.Closes[offset + i + 1] > fs.Closes[offset + i];
            if (signal == actual) correct++;
        }
        return testCount > 0 ? (float)correct / testCount : 0.5f;
    }

    // ── DB persistence ────────────────────────────────────────────────────────

    private async Task UpsertResultAsync(PredictionResult result)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.PredictionResults.FindAsync(result.Symbol);
        if (existing == null)
            db.PredictionResults.Add(result);
        else
        {
            existing.Interval         = result.Interval;
            existing.ComputedAt       = result.ComputedAt;
            existing.Status           = result.Status;
            existing.PredictedUp      = result.PredictedUp;
            existing.Probability      = result.Probability;
            existing.ModelAccuracy    = result.ModelAccuracy;
            existing.ModelAuc         = result.ModelAuc;
            existing.LogRegAccuracy   = result.LogRegAccuracy;
            existing.BenchmarkBuyHold = result.BenchmarkBuyHold;
            existing.BenchmarkSma     = result.BenchmarkSma;
            existing.TrainSamples     = result.TrainSamples;
            existing.TestSamples      = result.TestSamples;
            existing.TotalCandles     = result.TotalCandles;
            existing.ErrorMessage     = result.ErrorMessage;
        }
        await db.SaveChangesAsync();
    }

    // ── Settings helpers ──────────────────────────────────────────────────────

    private List<string> GetSymbols()
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
            "OneMinute"      => KlineInterval.OneMinute,
            "FiveMinutes"    => KlineInterval.FiveMinutes,
            "FifteenMinutes" => KlineInterval.FifteenMinutes,
            "ThirtyMinutes"  => KlineInterval.ThirtyMinutes,
            "FourHour"       => KlineInterval.FourHour,
            "OneDay"         => KlineInterval.OneDay,
            _                => KlineInterval.OneHour,
        };
    }

    private static PredictionResult MakeResult(string symbol, string interval, string status,
        int totalCandles, int train, int test, string? error = null) => new()
    {
        Symbol       = symbol,
        Interval     = interval,
        ComputedAt   = DateTime.UtcNow,
        Status       = status,
        TotalCandles = totalCandles,
        TrainSamples = train,
        TestSamples  = test,
        ErrorMessage = error,
    };
}
