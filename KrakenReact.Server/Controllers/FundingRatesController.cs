using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FundingRatesController : ControllerBase
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<FundingRatesController> _logger;

    public FundingRatesController(IHttpClientFactory http, ILogger<FundingRatesController> logger)
    {
        _http = http;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult> GetFundingRates(CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var resp = await client.GetAsync("https://futures.kraken.com/derivatives/api/v3/tickers", ct);
            if (!resp.IsSuccessStatusCode)
                return StatusCode((int)resp.StatusCode, "Kraken Futures API error");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tickers", out var tickers))
                return Ok(new List<object>());

            var result = new List<object>();
            foreach (var ticker in tickers.EnumerateArray())
            {
                if (!ticker.TryGetProperty("tag", out var tagEl)) continue;
                var tag = tagEl.GetString() ?? "";
                // Only include perpetual (PI_) instruments
                if (!ticker.TryGetProperty("symbol", out var symEl)) continue;
                var symbol = symEl.GetString() ?? "";
                if (!symbol.StartsWith("PI_", StringComparison.OrdinalIgnoreCase)) continue;

                var fundingRate = GetDecimal(ticker, "fundingRate");
                var fundingRatePrediction = GetDecimal(ticker, "fundingRatePrediction");
                var markPrice = GetDecimal(ticker, "markPrice");
                var indexPrice = GetDecimal(ticker, "indexPrice");
                var premium = indexPrice > 0 ? Math.Round((markPrice - indexPrice) / indexPrice * 100m, 4) : 0m;
                var openInterest = GetDecimal(ticker, "openInterest");
                var vol24h = GetDecimal(ticker, "vol24h");
                var lastPrice = GetDecimal(ticker, "last");

                // Annualise funding rate (8h funding → × 3 × 365)
                var annualisedFunding = fundingRate * 3 * 365 * 100;

                // Clean up display name: PI_XBTUSD → BTC/USD
                var displayName = symbol
                    .Replace("PI_", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("XBT", "BTC")
                    .Insert(symbol.Length - "PI_".Length - 3, "/");

                result.Add(new
                {
                    symbol,
                    displayName,
                    fundingRate        = Math.Round(fundingRate * 100m, 6),
                    fundingRatePct     = Math.Round(fundingRate * 100m, 6),
                    fundingRatePrediction = Math.Round(fundingRatePrediction * 100m, 6),
                    annualisedFundingPct = Math.Round(annualisedFunding, 2),
                    markPrice,
                    indexPrice,
                    premium,
                    lastPrice,
                    openInterest       = Math.Round(openInterest, 2),
                    vol24h             = Math.Round(vol24h, 2),
                });
            }

            result = result.OrderBy(r => ((dynamic)r).symbol).ToList();
            return Ok(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error fetching Kraken Futures tickers");
            return StatusCode(500, "Error fetching funding rates");
        }
    }

    private static decimal GetDecimal(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var val)) return 0m;
        return val.ValueKind switch
        {
            JsonValueKind.Number => val.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(val.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var ds) ? ds : 0m,
            _ => 0m,
        };
    }
}
