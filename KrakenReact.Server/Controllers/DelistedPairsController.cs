using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DelistedPairsController : ControllerBase
{
    private readonly TradingStateService _state;
    private readonly DelistedPriceService _delisted;

    public DelistedPairsController(TradingStateService state, DelistedPriceService delisted)
    {
        _state = state;
        _delisted = delisted;
    }

    [HttpGet]
    public ActionResult<List<DelistedPairDto>> GetDelistedPairs()
    {
        var result = new List<DelistedPairDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Get all active pairs from symbols
        var activePairs = _state.Symbols.Values.Select(s => s.WebsocketName).ToHashSet();

        // Get all pairs from price data (both active and delisted)
        foreach (var price in _state.Prices.Values)
        {
            var pairNoSlash = price.SymbolNoSlash;
            var hasHistoricalData = _delisted.HasPair(pairNoSlash);
            var isActive = activePairs.Contains(price.Symbol);
            var latestKline = price.LatestKline;

            result.Add(new DelistedPairDto
            {
                Symbol = price.Symbol,
                Status = isActive ? "active" : "delisted",
                LastPriceDate = latestKline?.OpenTime,
                LastPrice = latestKline?.Close,
                HasHistoricalData = hasHistoricalData
            });
            seen.Add(price.Symbol);
        }

        // Add pairs from delisted CSV that aren't in price data
        foreach (var pairNoSlash in _delisted.GetAvailablePairs())
        {
            // Convert to symbol with slash (e.g., MATICUSD -> MATIC/USD)
            var symbolWithSlash = InsertSlashInPair(pairNoSlash);

            if (!seen.Contains(symbolWithSlash))
            {
                var klines = _delisted.GetKlines(pairNoSlash, symbolWithSlash);
                result.Add(new DelistedPairDto
                {
                    Symbol = symbolWithSlash,
                    Status = "delisted",
                    LastPriceDate = klines?.LastOrDefault()?.OpenTime,
                    LastPrice = klines?.LastOrDefault()?.Close,
                    HasHistoricalData = true
                });
            }
        }

        return Ok(result.OrderBy(p => p.Status).ThenBy(p => p.Symbol).ToList());
    }

    private static string InsertSlashInPair(string pairNoSlash)
    {
        // Try common patterns: most pairs end with USD, GBP, EUR, USDT
        var quoteCurrencies = new[] { "USDT", "USDC", "USD", "GBP", "EUR" };
        foreach (var quote in quoteCurrencies)
        {
            if (pairNoSlash.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
            {
                var baseAsset = pairNoSlash[..^quote.Length];
                return $"{baseAsset}/{quote}";
            }
        }
        // Default: assume last 3 characters are quote currency
        if (pairNoSlash.Length > 3)
        {
            var baseAsset = pairNoSlash[..^3];
            var quote = pairNoSlash[^3..];
            return $"{baseAsset}/{quote}";
        }
        return pairNoSlash;
    }
}
