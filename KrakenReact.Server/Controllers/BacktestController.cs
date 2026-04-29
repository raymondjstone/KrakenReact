using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/backtest")]
public class BacktestController : ControllerBase
{
    private readonly TradingStateService _state;

    public BacktestController(TradingStateService state) => _state = state;

    /// <summary>
    /// Simulates the auto-trade buy/sell rule against historical daily klines for a symbol.
    /// Returns a list of simulated entry/exit pairs with P/L.
    /// </summary>
    [HttpGet]
    public IActionResult RunBacktest([FromQuery] string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        if (!_state.Prices.TryGetValue(symbol, out var instrument))
            return NotFound(new { message = $"Symbol {symbol} not found" });

        var klines = instrument.GetKlineSnapshot()
            .Where(k => k.Interval == "OneDay")
            .OrderBy(k => k.OpenTime)
            .ToList();

        if (klines.Count < 60)
            return Ok(new { symbol, trades = Array.Empty<object>(), summary = new { message = "Insufficient data (< 60 daily bars)" } });

        // Simulate the auto-trade rule: buy when today's avg < 7-day avg (price dipped)
        var trades = new List<object>();
        decimal? entryPrice = null;
        DateTime? entryDate = null;
        decimal cash = 10000m;
        decimal position = 0m;

        for (int i = 30; i < klines.Count; i++)
        {
            var close = klines[i].Close;
            var avg7 = klines.Skip(i - 7).Take(7).Average(k => k.Close);
            var avg1 = close;
            var weekDayDiff = avg7 > 0 ? (avg1 * 100 / avg7) : 100m;

            if (entryPrice == null && close > 0 && weekDayDiff < 100m && cash > 0)
            {
                // Buy signal: today's price below 7-day average
                position = cash / close;
                entryPrice = close;
                entryDate = klines[i].OpenTime;
                cash = 0m;
            }
            else if (entryPrice != null && weekDayDiff >= 100m)
            {
                // Sell signal: today's price above 7-day average
                var exitPrice = close;
                var pl = (exitPrice - entryPrice.Value) / entryPrice.Value * 100;
                cash = position * exitPrice;
                trades.Add(new
                {
                    entryDate,
                    entryPrice,
                    exitDate = klines[i].OpenTime,
                    exitPrice,
                    plPct = Math.Round(pl, 2),
                    cashAfter = Math.Round(cash, 2),
                });
                position = 0m;
                entryPrice = null;
                entryDate = null;
            }
        }

        var totalPl = trades.Count > 0 ? Math.Round((cash - 10000m) / 10000m * 100, 2) : 0m;
        var winTrades = trades.Cast<dynamic>().Count(t => (decimal)t.plPct > 0);
        var winRate = trades.Count > 0 ? Math.Round((decimal)winTrades / trades.Count * 100, 1) : 0m;

        return Ok(new
        {
            symbol,
            trades,
            summary = new
            {
                tradeCount = trades.Count,
                winRate,
                totalPlPct = totalPl,
                finalCash = Math.Round(cash + position * (klines.LastOrDefault()?.Close ?? 0), 2),
                dataRange = new { from = klines.First().OpenTime, to = klines.Last().OpenTime },
            }
        });
    }

    /// <summary>GET /api/backtest/walkforward?symbol=X&amp;trainSize=60&amp;testSize=30 — rolling out-of-sample windows</summary>
    [HttpGet("walkforward")]
    public IActionResult WalkForward([FromQuery] string symbol, [FromQuery] int trainSize = 60, [FromQuery] int testSize = 30)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        if (!_state.Prices.TryGetValue(symbol, out var instrument))
            return NotFound(new { message = $"Symbol {symbol} not found" });

        trainSize = Math.Clamp(trainSize, 30, 365);
        testSize = Math.Clamp(testSize, 10, 90);

        var klines = instrument.GetKlineSnapshot()
            .Where(k => k.Interval == "OneDay")
            .OrderBy(k => k.OpenTime)
            .ToList();

        int windowSize = trainSize + testSize;
        if (klines.Count < windowSize + 10)
            return Ok(new { symbol, windows = Array.Empty<object>(), message = "Insufficient data" });

        var windows = new List<object>();
        int step = testSize;

        for (int start = 0; start + windowSize <= klines.Count; start += step)
        {
            var testKlines = klines.Skip(start + trainSize).Take(testSize).ToList();
            if (testKlines.Count < 5) break;

            // Run the buy/sell strategy on the test window (same logic as RunBacktest)
            // Use training window end as context for 7-day avg
            var allUpToTestEnd = klines.Take(start + windowSize).ToList();
            decimal? entryPrice = null;
            decimal cash2 = 10000m;
            decimal position2 = 0m;
            var tradePairs = new List<(decimal entry, decimal exit)>();

            for (int i = start + trainSize; i < start + windowSize && i < klines.Count; i++)
            {
                var close = klines[i].Close;
                var avg7 = klines.Skip(Math.Max(0, i - 7)).Take(7).Average(k => k.Close);
                var weekDayDiff = avg7 > 0 ? close * 100 / avg7 : 100m;

                if (entryPrice == null && close > 0 && weekDayDiff < 100m && cash2 > 0)
                {
                    position2 = cash2 / close;
                    entryPrice = close;
                    cash2 = 0m;
                }
                else if (entryPrice != null && weekDayDiff >= 100m)
                {
                    tradePairs.Add((entryPrice.Value, close));
                    cash2 = position2 * close;
                    position2 = 0m;
                    entryPrice = null;
                }
            }

            var winCount = tradePairs.Count(t => t.exit > t.entry);
            var winRate2 = tradePairs.Count > 0 ? Math.Round((double)winCount / tradePairs.Count * 100, 1) : 0.0;
            var finalVal = cash2 + position2 * (testKlines.Last().Close);
            var returnPct = Math.Round((double)(finalVal - 10000m) / 10000.0 * 100, 2);

            windows.Add(new
            {
                from = testKlines.First().OpenTime,
                to = testKlines.Last().OpenTime,
                tradeCount = tradePairs.Count,
                winRate = winRate2,
                returnPct,
            });
        }

        var avgWinRate = windows.Count > 0 ? windows.Cast<dynamic>().Average(w => (double)w.winRate) : 0.0;
        var avgReturn = windows.Count > 0 ? windows.Cast<dynamic>().Average(w => (double)w.returnPct) : 0.0;

        return Ok(new
        {
            symbol,
            trainSize,
            testSize,
            windowCount = windows.Count,
            windows,
            summary = new
            {
                avgWinRate = Math.Round(avgWinRate, 1),
                avgReturnPct = Math.Round(avgReturn, 2),
            }
        });
    }
}
