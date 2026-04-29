using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/predictions")]
public class PredictionController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly TradingStateService _state;

    public PredictionController(KrakenDbContext db, IBackgroundJobClient backgroundJobClient, TradingStateService state)
    {
        _db = db;
        _backgroundJobClient = backgroundJobClient;
        _state = state;
    }

    /// <summary>GET /api/predictions — latest result per symbol</summary>
    [HttpGet]
    public async Task<IActionResult> GetPredictions()
    {
        var results = await _db.PredictionResults.AsNoTracking().ToListAsync();
        return Ok(results);
    }

    /// <summary>POST /api/predictions/trigger — enqueue the job immediately</summary>
    [HttpPost("trigger")]
    public IActionResult TriggerNow()
    {
        _backgroundJobClient.Enqueue<PredictionJob>(j => j.ExecuteAsync(CancellationToken.None));
        return Ok(new { message = "Prediction job enqueued" });
    }

    /// <summary>POST /api/predictions/trigger/single?symbol=XBT/USD — enqueue for one symbol</summary>
    [HttpPost("trigger/single")]
    public IActionResult TriggerSingle([FromQuery] string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        _backgroundJobClient.Enqueue<PredictionJob>(j => j.ExecuteSingleAsync(symbol, CancellationToken.None));
        return Ok(new { message = $"Prediction enqueued for {symbol}" });
    }

    /// <summary>DELETE /api/predictions?symbol=XBT/USD — remove a prediction result</summary>
    [HttpDelete]
    public async Task<IActionResult> DeletePrediction([FromQuery] string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        var existing = await _db.PredictionResults.FindAsync(symbol);
        if (existing == null) return NotFound();

        _db.PredictionResults.Remove(existing);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>GET /api/predictions/history/{symbol} — last N historical snapshots</summary>
    [HttpGet("history/{symbol}")]
    public async Task<IActionResult> GetHistory(string symbol, [FromQuery] int limit = 30)
    {
        symbol = Uri.UnescapeDataString(symbol);
        limit = Math.Clamp(limit, 1, 200);
        var history = await _db.PredictionHistories
            .Where(h => h.Symbol == symbol)
            .OrderByDescending(h => h.ComputedAt)
            .Take(limit)
            .Select(h => new { h.ComputedAt, h.PredictedUp, h.Probability, h.ModelAccuracy, h.WalkForwardAccuracy, h.Interval })
            .ToListAsync();
        return Ok(history);
    }

    /// <summary>GET /api/predictions/accuracy/{symbol} — hit-rate of past predictions vs actual prices</summary>
    [HttpGet("accuracy/{symbol}")]
    public async Task<IActionResult> GetAccuracy(string symbol, [FromQuery] int limit = 50)
    {
        symbol = Uri.UnescapeDataString(symbol);
        limit = Math.Clamp(limit, 5, 200);

        var history = await _db.PredictionHistories
            .Where(h => h.Symbol == symbol)
            .OrderByDescending(h => h.ComputedAt)
            .Take(limit)
            .ToListAsync();

        if (history.Count < 3)
            return Ok(new { symbol, sampleCount = history.Count, hitRate = (double?)null, message = "Insufficient history" });

        // Resolve kline data from state
        var priceKey = _state.ResolveSymbolKey(symbol);
        _state.Prices.TryGetValue(priceKey, out var priceItem);
        var klines = priceItem?.GetKlineSnapshot()
            .OrderBy(k => k.OpenTime)
            .ToList() ?? new List<Models.DerivedKline>();

        int hits = 0, evaluated = 0;
        foreach (var h in history)
        {
            // Find close price at prediction time
            var atPred = klines.LastOrDefault(k => k.OpenTime <= h.ComputedAt);
            // Find close price one interval later (1 candle)
            var intervalMs = IntervalToMs(h.Interval);
            var targetTime = h.ComputedAt.AddMilliseconds(intervalMs);
            var atTarget = klines.FirstOrDefault(k => k.OpenTime >= targetTime);

            if (atPred == null || atTarget == null || atPred.Close <= 0) continue;
            var actualUp = atTarget.Close > atPred.Close;
            if (actualUp == h.PredictedUp) hits++;
            evaluated++;
        }

        return Ok(new
        {
            symbol,
            sampleCount = history.Count,
            evaluatedCount = evaluated,
            hitRate = evaluated > 0 ? Math.Round((double)hits / evaluated * 100, 1) : (double?)null,
        });
    }

    /// <summary>GET /api/predictions/kelly/{symbol} — Kelly fraction position sizing</summary>
    [HttpGet("kelly/{symbol}")]
    public async Task<IActionResult> GetKelly(string symbol, [FromQuery] int limit = 50)
    {
        symbol = Uri.UnescapeDataString(symbol);
        limit = Math.Clamp(limit, 10, 200);

        var history = await _db.PredictionHistories
            .Where(h => h.Symbol == symbol)
            .OrderByDescending(h => h.ComputedAt)
            .Take(limit)
            .ToListAsync();

        if (history.Count < 10)
            return Ok(new { symbol, kellyFraction = (double?)null, message = "Insufficient history (need ≥10)" });

        var priceKey = _state.ResolveSymbolKey(symbol);
        _state.Prices.TryGetValue(priceKey, out var priceItem);
        var klines = priceItem?.GetKlineSnapshot().OrderBy(k => k.OpenTime).ToList() ?? new List<Models.DerivedKline>();

        var wins = new List<double>(); var losses = new List<double>();
        foreach (var h in history)
        {
            var atPred = klines.LastOrDefault(k => k.OpenTime <= h.ComputedAt);
            var intervalMs = IntervalToMs(h.Interval);
            var atTarget = klines.FirstOrDefault(k => k.OpenTime >= h.ComputedAt.AddMilliseconds(intervalMs));
            if (atPred == null || atTarget == null || atPred.Close <= 0) continue;

            var actualReturn = (double)((atTarget.Close - atPred.Close) / atPred.Close);
            var predicted = h.PredictedUp;
            if ((predicted && actualReturn > 0) || (!predicted && actualReturn < 0))
                wins.Add(Math.Abs(actualReturn));
            else
                losses.Add(Math.Abs(actualReturn));
        }

        if (wins.Count + losses.Count < 5)
            return Ok(new { symbol, kellyFraction = (double?)null, message = "Insufficient evaluated predictions" });

        var winRate = (double)wins.Count / (wins.Count + losses.Count);
        var avgWin = wins.Count > 0 ? wins.Average() : 0.01;
        var avgLoss = losses.Count > 0 ? losses.Average() : 0.01;
        var winLossRatio = avgLoss > 0 ? avgWin / avgLoss : 1.0;
        var kelly = winRate - (1 - winRate) / winLossRatio;
        var halfKelly = Math.Max(0, kelly / 2); // half-Kelly for risk management

        var totalPortfolio = (double)_state.Balances.Values.Sum(b => b.LatestValue);
        var suggestedUsd = Math.Round(totalPortfolio * halfKelly, 2);

        return Ok(new
        {
            symbol,
            winRate = Math.Round(winRate * 100, 1),
            winLossRatio = Math.Round(winLossRatio, 3),
            kellyFraction = Math.Round(kelly * 100, 2),
            halfKellyFraction = Math.Round(halfKelly * 100, 2),
            suggestedUsd = Math.Max(0, suggestedUsd),
            evaluatedCount = wins.Count + losses.Count,
        });
    }

    /// <summary>GET /api/predictions/confidence/{symbol} — hit-rate per probability bucket (deciles)</summary>
    [HttpGet("confidence/{symbol}")]
    public async Task<IActionResult> GetConfidenceHistogram(string symbol, [FromQuery] int limit = 100)
    {
        symbol = Uri.UnescapeDataString(symbol);
        limit = Math.Clamp(limit, 10, 500);

        var history = await _db.PredictionHistories
            .Where(h => h.Symbol == symbol)
            .OrderByDescending(h => h.ComputedAt)
            .Take(limit)
            .ToListAsync();

        if (history.Count < 5)
            return Ok(new { symbol, buckets = Array.Empty<object>(), message = "Insufficient history" });

        var priceKey = _state.ResolveSymbolKey(symbol);
        _state.Prices.TryGetValue(priceKey, out var priceItem);
        var klines = priceItem?.GetKlineSnapshot().OrderBy(k => k.OpenTime).ToList() ?? new List<Models.DerivedKline>();

        // Build per-prediction results
        var evals = new List<(double prob, bool hit)>();
        foreach (var h in history)
        {
            var atPred = klines.LastOrDefault(k => k.OpenTime <= h.ComputedAt);
            var atTarget = klines.FirstOrDefault(k => k.OpenTime >= h.ComputedAt.AddMilliseconds(IntervalToMs(h.Interval)));
            if (atPred == null || atTarget == null || atPred.Close <= 0) continue;
            var actualUp = atTarget.Close > atPred.Close;
            evals.Add(((double)h.Probability, actualUp == h.PredictedUp));
        }

        // Bucket by decile 0-10%, 10-20% ... 90-100%
        var buckets = Enumerable.Range(0, 10).Select(i =>
        {
            var low = i * 10;
            var high = (i + 1) * 10;
            var inBucket = evals.Where(e => e.prob * 100 >= low && (i == 9 ? e.prob * 100 <= 100 : e.prob * 100 < high)).ToList();
            var hits = inBucket.Count(e => e.hit);
            return new
            {
                label = $"{low}–{high}%",
                count = inBucket.Count,
                hits,
                hitRate = inBucket.Count > 0 ? Math.Round((double)hits / inBucket.Count * 100, 1) : (double?)null,
            };
        }).ToList();

        return Ok(new { symbol, buckets, evaluatedCount = evals.Count });
    }

    private static long IntervalToMs(string interval) => interval switch
    {
        "OneMinute"      => 60_000,
        "FiveMinutes"    => 5  * 60_000,
        "FifteenMinutes" => 15 * 60_000,
        "ThirtyMinutes"  => 30 * 60_000,
        "FourHour"       => 4  * 60 * 60_000L,
        "OneDay"         => 24 * 60 * 60_000L,
        _                => 60 * 60_000L,
    };

    /// <summary>GET /api/predictions/regime/{symbol} — market regime (trending/ranging/volatile)</summary>
    [HttpGet("regime/{symbol}")]
    public IActionResult GetRegime(string symbol)
    {
        symbol = Uri.UnescapeDataString(symbol);
        var priceKey = _state.ResolveSymbolKey(symbol);
        if (!_state.Prices.TryGetValue(priceKey, out var priceItem))
            return NotFound(new { message = "Symbol not found" });

        var klines = priceItem.GetKlineSnapshot()
            .OrderBy(k => k.OpenTime)
            .ToList();

        if (klines.Count < 30)
            return Ok(new { symbol, regime = "unknown", adx = (double?)null, bbWidth = (double?)null });

        var closes = klines.Select(k => (float)k.Close).ToArray();
        var highs  = klines.Select(k => (float)k.High).ToArray();
        var lows   = klines.Select(k => (float)k.Low).ToArray();

        var adxValues = Services.FeatureEngineering.ComputeAdx(highs, lows, closes);
        var adx = adxValues.Length > 0 ? adxValues[^1] : 0f;

        var (upper, lower, mid) = Services.FeatureEngineering.ComputeBollingerBands(closes);
        var lastClose = closes[^1];
        var bbWidth = lastClose > 0 && upper.Length > 0
            ? (upper[^1] - lower[^1]) / lastClose
            : 0f;

        string regime;
        if (adx > 30)
            regime = bbWidth > 0.06 ? "volatile-trending" : "trending";
        else if (bbWidth > 0.08)
            regime = "volatile";
        else
            regime = "ranging";

        return Ok(new
        {
            symbol,
            regime,
            adx = Math.Round(adx, 2),
            bbWidth = Math.Round(bbWidth * 100, 2),
        });
    }

    /// <summary>GET /api/predictions/multitf/{symbol} — multi-timeframe results for a symbol</summary>
    [HttpGet("multitf/{symbol}")]
    public async Task<IActionResult> GetMultiTf(string symbol)
    {
        symbol = Uri.UnescapeDataString(symbol);
        var results = await _db.MultiTfPredictionResults
            .Where(r => r.Symbol == symbol)
            .AsNoTracking()
            .ToListAsync();
        return Ok(results);
    }

    /// <summary>POST /api/predictions/multitf/{symbol}/trigger — enqueue multi-tf job for a symbol</summary>
    [HttpPost("multitf/{symbol}/trigger")]
    public IActionResult TriggerMultiTf(string symbol)
    {
        symbol = Uri.UnescapeDataString(symbol);
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });
        _backgroundJobClient.Enqueue<MultiTfPredictionJob>(j => j.ExecuteAsync(symbol, CancellationToken.None));
        return Ok(new { message = $"Multi-TF prediction enqueued for {symbol}" });
    }

    /// <summary>GET /api/predictions/settings — current prediction config</summary>
    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        var availableCurrencies = _state.Symbols.Values
            .Select(s => s.WebsocketName.Contains('/') ? s.WebsocketName.Split('/')[1] : "")
            .Where(c => c.Length > 0)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        return Ok(new
        {
            symbols             = _state.PredictionSymbols,
            interval            = _state.PredictionInterval,
            mode                = _state.PredictionMode,
            currency            = _state.PredictionCurrency,
            availableCurrencies,
        });
    }
}
