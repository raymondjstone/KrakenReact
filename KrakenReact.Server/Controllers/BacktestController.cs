using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/backtest")]
public class BacktestController : ControllerBase
{
    private readonly TradingStateService _state;

    public BacktestController(TradingStateService state) => _state = state;

    /// <summary>The usual Kraken taker fee, charged on every simulated buy and sell unless the caller says otherwise.</summary>
    public const decimal DefaultFeePct = 0.26m;

    private List<DerivedKline>? DailyKlines(string symbol)
    {
        // "BTC/USD" and "XBT/USD" are the same market; every other endpoint resolves the name, so this one does too
        if (!_state.Prices.TryGetValue(_state.ResolveSymbolKey(symbol), out var instrument)) return null;
        return instrument.GetKlineSnapshot()
            .Where(k => k.Interval == "OneDay")
            .OrderBy(k => k.OpenTime)
            .ToList();
    }

    /// <summary>
    /// Simulates the auto-trade buy/sell rule against historical daily klines for a symbol.
    /// Returns a list of simulated entry/exit pairs with P/L. <c>feePct</c> (default 0.26) is charged on each buy and sell.
    /// </summary>
    [HttpGet]
    public IActionResult RunBacktest([FromQuery] string symbol, [FromQuery] decimal feePct = DefaultFeePct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        var klines = DailyKlines(symbol);
        if (klines == null) return NotFound(new { message = $"Symbol {symbol} not found" });

        if (klines.Count < 60)
            return Ok(new { symbol, trades = Array.Empty<object>(), summary = new { message = "Insufficient data (< 60 daily bars)" } });

        var result = BacktestEngine.Run(klines, 30, klines.Count, feePct);
        var trades = result.Trades;

        // Measured on the final value, so a position still open at the end counts at the last close instead of reading as a total loss
        var totalPl = Math.Round((result.FinalValue - BacktestEngine.StartingCash) / BacktestEngine.StartingCash * 100, 2);
        var winRate = trades.Count > 0 ? Math.Round((decimal)trades.Count(t => t.PlPct > 0) / trades.Count * 100, 1) : 0m;

        return Ok(new
        {
            symbol,
            trades,
            summary = new
            {
                tradeCount = trades.Count,
                winRate,
                totalPlPct = totalPl,
                finalCash = Math.Round(result.FinalValue, 2),
                openPosition = result.OpenPosition,
                feePct,
                dataRange = new { from = klines.First().OpenTime, to = klines.Last().OpenTime },
            }
        });
    }

    /// <summary>GET /api/backtest/walkforward?symbol=X&amp;trainSize=60&amp;testSize=30 — rolling out-of-sample windows</summary>
    [HttpGet("walkforward")]
    public IActionResult WalkForward([FromQuery] string symbol, [FromQuery] int trainSize = 60, [FromQuery] int testSize = 30, [FromQuery] decimal feePct = DefaultFeePct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        var klines = DailyKlines(symbol);
        if (klines == null) return NotFound(new { message = $"Symbol {symbol} not found" });

        trainSize = Math.Clamp(trainSize, 30, 365);
        testSize = Math.Clamp(testSize, 10, 90);

        int windowSize = trainSize + testSize;
        if (klines.Count < windowSize + 10)
            return Ok(new { symbol, windows = Array.Empty<object>(), message = "Insufficient data" });

        var windows = new List<WalkForwardWindow>();
        for (int start = 0; start + windowSize <= klines.Count; start += testSize)
        {
            var from = start + trainSize;
            var to = start + windowSize;
            if (to - from < 5) break;

            // The 7-candle average looks back into the training span for context; only the test span is traded
            var result = BacktestEngine.Run(klines, from, to, feePct);
            var winRate = result.Trades.Count > 0
                ? Math.Round((double)result.Trades.Count(t => t.PlPct > 0) / result.Trades.Count * 100, 1) : 0.0;
            var returnPct = Math.Round((double)(result.FinalValue - BacktestEngine.StartingCash) / (double)BacktestEngine.StartingCash * 100, 2);

            windows.Add(new WalkForwardWindow(klines[from].OpenTime, klines[to - 1].OpenTime, result.Trades.Count, winRate, returnPct));
        }

        var avgWinRate = windows.Count > 0 ? windows.Average(w => w.WinRate) : 0.0;
        var avgReturn = windows.Count > 0 ? windows.Average(w => w.ReturnPct) : 0.0;

        return Ok(new
        {
            symbol,
            trainSize,
            testSize,
            feePct,
            windowCount = windows.Count,
            windows,
            summary = new
            {
                avgWinRate = Math.Round(avgWinRate, 1),
                avgReturnPct = Math.Round(avgReturn, 2),
            }
        });
    }

    public sealed record WalkForwardWindow(DateTime From, DateTime To, int TradeCount, double WinRate, double ReturnPct);
}
