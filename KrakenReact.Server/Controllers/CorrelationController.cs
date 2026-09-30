using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/correlation")]
public class CorrelationController : ControllerBase
{
    private readonly TradingStateService _state;

    public CorrelationController(TradingStateService state)
    {
        _state = state;
    }

    /// <summary>GET /api/correlation?symbols=A,B,C&amp;days=30 — Pearson correlation matrix over daily klines</summary>
    [HttpGet]
    public IActionResult GetMatrix([FromQuery] string? symbols, [FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 7, 365);

        // Derive symbol list: use balances if none specified
        IEnumerable<string> symList;
        if (!string.IsNullOrWhiteSpace(symbols))
        {
            symList = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
        }
        else
        {
            symList = _state.Balances.Values
                .Where(b => b.Total > 0.0001m && !IsStablecoin(b.Asset))
                .Select(b => b.Asset + "/USD");
        }

        var symbolArray = symList.Distinct().ToArray();
        if (symbolArray.Length < 2) return BadRequest("Need at least 2 symbols");
        if (symbolArray.Length > 20) return BadRequest("Maximum 20 symbols");

        var since = DateTime.UtcNow.Date.AddDays(-days - 5); // extra buffer for alignment
        var returns = new Dictionary<string, Dictionary<DateTime, double>>();

        foreach (var sym in symbolArray)
        {
            var priceKey = _state.ResolveSymbolKey(sym);
            if (!_state.Prices.TryGetValue(priceKey, out var priceItem)) continue;

            var klines = priceItem.GetKlineSnapshot()
                .Where(k => k.Interval == "OneDay" && k.OpenTime >= since && k.Close > 0)
                .OrderBy(k => k.OpenTime)
                .ToList();

            if (klines.Count < 3) continue;

            var dailyReturns = CorrelationMath.DailyReturns(klines);
            if (dailyReturns.Count >= CorrelationMath.MinimumOverlap)
                returns[sym] = dailyReturns;
        }

        var keys = returns.Keys.ToArray();
        if (keys.Length < 2) return Ok(new { symbols = keys, matrix = Array.Empty<double[]>() });

        var matrix = new double[keys.Length][];
        for (int i = 0; i < keys.Length; i++)
        {
            matrix[i] = new double[keys.Length];
            for (int j = 0; j < keys.Length; j++)
            {
                matrix[i][j] = i == j ? 1.0 : CorrelationMath.Pearson(returns[keys[i]], returns[keys[j]]);
            }
        }

        return Ok(new { symbols = keys, matrix, days });
    }

    private static bool IsStablecoin(string asset) =>
        asset is "USD" or "USDT" or "USDC" or "USDQ" or "EUR" or "GBP" or "CAD" or "AUD" or "JPY" or "CHF";
}
