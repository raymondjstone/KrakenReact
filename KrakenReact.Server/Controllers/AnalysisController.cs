using KrakenReact.Server.Analysis;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/analysis")]
public class AnalysisController : ControllerBase
{
    private readonly MarketAnalysisService _analysis;
    private readonly MinuteCandleJob _minuteCandles;
    private readonly TradingStateService _state;

    /// <summary>The fewest stored candles a market needs before it is offered for analysis.</summary>
    private const int MinimumCandles = 200;

    /// <summary>
    /// Minute bars needed before the grade model is worth running — roughly a month, which is what
    /// it takes for a two-hour fall on a four-hour baseline to have any history around it.
    /// </summary>
    private const int MinuteBarsForGrading = 43_200;

    public AnalysisController(MarketAnalysisService analysis, MinuteCandleJob minuteCandles, TradingStateService state)
    {
        _analysis = analysis;
        _minuteCandles = minuteCandles;
        _state = state;
    }

    /// <summary>
    /// GET /api/analysis/chart-levels?symbol=XBT/USD&amp;interval=60 — the support and resistance levels to
    /// draw on a chart. The interval is the chart's own key (1, 5, 15, 30, 60, 240, 1D, 1W). Levels are
    /// never built from anything finer than hourly bars, because a three-range swing on minute bars is
    /// noise rather than a level: intraday charts share the hourly levels, 4H uses four-hour bars, and
    /// 1D and 1W use daily bars.
    /// </summary>
    [HttpGet("chart-levels")]
    public async Task<IActionResult> GetChartLevels(
        [FromQuery] string symbol,
        [FromQuery] string? interval = null,
        [FromQuery] int minimumTouches = ChartLevels.MajorLevelMinimumTouches,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        try
        {
            var storedInterval = interval switch
            {
                "240" => "FourHour",
                "1D" or "1W" => "OneDay",
                _ => "OneHour",
            };
            var levels = await _analysis.GetChartLevelsAsync(
                _state.ResolveSymbolKey(symbol), storedInterval, Math.Clamp(minimumTouches, 2, 100), ct);
            return Ok(new { interval = storedInterval, levels });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/analysis/minute-coverage — how much one-minute history each traded pair has.
    /// <para>
    /// Minute history can only be collected going forward, so this is a progress report: the
    /// minute-resolution analysis stays unavailable until these series are deep enough.
    /// </para>
    /// </summary>
    [HttpGet("minute-coverage")]
    public async Task<IActionResult> GetMinuteCoverage(CancellationToken ct)
    {
        try
        {
            var coverage = await _minuteCandles.GetCoverageAsync(ct);
            return Ok(new
            {
                barsNeededForGrading = MinuteBarsForGrading,
                trackedPairs = coverage.Count,
                pairsReady = coverage.Count(c => c.CandleCount >= MinuteBarsForGrading),
                coverage = coverage.Select(c => new
                {
                    c.Pair,
                    c.CandleCount,
                    c.FirstOpenTime,
                    c.LastOpenTime,
                    c.SpanDays,
                    readyForGrading = c.CandleCount >= MinuteBarsForGrading,
                    percentComplete = Math.Round(Math.Min(100m, (decimal)c.CandleCount / MinuteBarsForGrading * 100m), 1),
                }),
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>GET /api/analysis/symbols — markets with enough stored candles to analyse</summary>
    [HttpGet("symbols")]
    public async Task<IActionResult> GetSymbols(CancellationToken ct)
    {
        try
        {
            return Ok(await _analysis.ListAnalysableAsync(MinimumCandles, ct));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/analysis/market?symbol=XBT/USD&amp;interval=OneHour — the full analysis report.
    /// </summary>
    [HttpGet("market")]
    public async Task<IActionResult> GetMarket(
        [FromQuery] string symbol,
        [FromQuery] string interval = "OneHour",
        [FromQuery] decimal minimumDropPercent = 10m,
        [FromQuery] decimal minimumSurgePercent = 10m,
        [FromQuery] decimal pivotReversalAtrMultiple = 3m,
        [FromQuery] decimal plummetQuietnessMultiple = 0.75m,
        [FromQuery] decimal minimumFallBelowAveragePercent = 2m,
        [FromQuery] decimal maximumHighAboveAveragePercent = 20m,
        [FromQuery] decimal stake = 1000m,
        [FromQuery] decimal feePercentPerSide = 0.26m,
        [FromQuery] decimal spreadAllowancePercent = 0.1m,
        [FromQuery] int tradeWindowBars = 72,
        [FromQuery] decimal entryTargetPercent = 8m,
        [FromQuery] decimal entryStopPercent = 6m,
        [FromQuery] bool useZeroLag = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        try
        {
            var candles = await _analysis.LoadCandlesAsync(symbol, interval, ct);
            var options = new AnalysisOptions
            {
                MinimumDropFraction = Math.Clamp(minimumDropPercent, 1m, 90m) / 100m,
                MinimumSurgeFraction = Math.Clamp(minimumSurgePercent, 1m, 500m) / 100m,
                PivotReversalAtrMultiple = Math.Clamp(pivotReversalAtrMultiple, 0.5m, 20m),
                PlummetQuietnessMultiple = Math.Clamp(plummetQuietnessMultiple, 0m, 10m),
                MinimumFallBelowAverageFraction = Math.Clamp(minimumFallBelowAveragePercent, 0m, 100m) / 100m,
                MaximumReferenceHighAboveAverageFraction = Math.Clamp(maximumHighAboveAveragePercent, 0m, 1000m) / 100m,
                Stake = Math.Clamp(stake, 1m, 10_000_000m),
                FeeFractionPerSide = Math.Clamp(feePercentPerSide, 0m, 5m) / 100m,
                SpreadAllowanceFraction = Math.Clamp(spreadAllowancePercent, 0m, 5m) / 100m,
                TradeWindowBars = Math.Clamp(tradeWindowBars, 2, 2000),
                EntryTargetFraction = Math.Clamp(entryTargetPercent, 0.5m, 100m) / 100m,
                EntryStopFraction = Math.Clamp(entryStopPercent, 0.5m, 100m) / 100m,
                UseZeroLagMovingAverages = useZeroLag,
            };
            return Ok(_analysis.Analyse(symbol, interval, candles, options));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
