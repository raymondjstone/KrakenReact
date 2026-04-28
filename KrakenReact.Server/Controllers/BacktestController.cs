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
}
