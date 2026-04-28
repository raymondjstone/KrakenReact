using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PricesController : ControllerBase
{
    private readonly TradingStateService _state;
    private readonly KrakenRestService _kraken;
    private readonly DbMethods _db;
    private readonly ILogger<PricesController> _logger;

    public PricesController(TradingStateService state, KrakenRestService kraken, DbMethods db, ILogger<PricesController> logger)
    {
        _state = state;
        _kraken = kraken;
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public ActionResult<List<PriceDto>> GetAll()
    {
        var prices = _state.GetPriceSnapshot().Select(p =>
        {
            var latest = p.LatestKline;
            var orders = _state.Orders.Values.Where(o => o.Symbol.StartsWith(p.SymbolNoSlash) && o.Status == "Closed" && o.Side == "Buy").ToList();
            var avgBuyPrice = orders.Any() ? Math.Round(orders.Sum(o => o.AveragePrice) / orders.Count, 9) : 0m;

            var normalizedBase = TradingStateService.NormalizeAsset(p.Base);
            var normalizedCcy = TradingStateService.NormalizeAsset(p.CCY);
            return new PriceDto
            {
                Symbol = p.Symbol,
                DisplaySymbol = normalizedBase + "/" + normalizedCcy,
                Base = normalizedBase,
                CCY = normalizedCcy,
                CoinType = p.CoinType,
                ClosePrice = latest?.Close,
                OpenPrice = latest?.Open,
                HighPrice = latest?.High,
                LowPrice = latest?.Low,
                Volume = latest?.Volume,
                VolumeWeightedAveragePrice = latest?.VolumeWeightedAveragePrice,
                TradeCount = latest?.TradeCount,
                OpenTime = latest?.OpenTime,
                Age = p.Age,
                KrakenNewPricesLoaded = p.KrakenNewPricesLoaded,
                ClosePriceMovement = p.CloseMovementDiff(1),
                ClosePriceMovementWeek = p.CloseMovementDiff(7),
                ClosePriceMovementMonth = p.CloseMovementDiff(31),
                ClosePriceDifference = p.ClosePriceDiff(1),
                ClosePriceDifferenceWeek = p.ClosePriceDiff(7),
                ClosePriceDifferenceMonth = p.ClosePriceDiff(31),
                AvgPriceDay = p.ClosePriceAverage(1),
                AvgPriceWeek = p.ClosePriceAverage(7),
                AvgPriceMonth = p.ClosePriceAverage(31),
                AvgPriceYear = p.ClosePriceAverage(365),
                WeightedPrice = p.WeightedPrice,
                WeightedPricePercentage = p.WeightedPricePercentage,
                AverageBuyPrice = avgBuyPrice,
                PriceLowerThanBuy = avgBuyPrice > 0 && avgBuyPrice >= (latest?.Close ?? 0) && p.KrakenNewPricesLoadedEver,
                BestBid = p.TickerData?.BestBidPrice,
                BestAsk = p.TickerData?.BestAskPrice
            };
        }).ToList();
        return Ok(prices);
    }

    [HttpGet("{symbol}/klines")]
    public async Task<ActionResult<List<KlineDto>>> GetKlines(string symbol, [FromQuery] string? interval = null)
    {
        // symbol comes URL-encoded, e.g. "SOL%2FUSD" -> "SOL/USD"
        symbol = Uri.UnescapeDataString(symbol);

        // Resolve normalized names (e.g. "BTC/USD") back to Kraken keys (e.g. "XBT/USD")
        var resolvedSymbol = _state.ResolveSymbolKey(symbol);
        var cleanSymbol = resolvedSymbol.Replace(".F/", "/").Replace(".B/", "/");
        _logger.LogInformation("Klines request: symbol={Symbol}, resolved={Resolved}, clean={Clean}, interval={Interval}",
            symbol, resolvedSymbol, cleanSymbol, interval);

        // Always try to fetch from Kraken API first for all intervals (including 1D)
        var krakenInterval = ParseInterval(interval);
        if (krakenInterval != null)
        {
            var since = GetSinceForInterval(krakenInterval.Value);
            var result = await _kraken.GetKlinesAsync(cleanSymbol, krakenInterval.Value, since);
            _logger.LogInformation("Klines API result for {Clean}: {Count} candles", cleanSymbol, result.Count());
            if (result.Any())
            {
                return Ok(result.Select(k => new KlineDto
                {
                    OpenTime = k.OpenTime, Open = k.OpenPrice, High = k.HighPrice,
                    Low = k.LowPrice, Close = k.ClosePrice, Volume = k.Volume
                }).ToList());
            }
        }

        // Fallback to in-memory (if available)
        if (_state.Prices.TryGetValue(resolvedSymbol, out var priceItem))
        {
            var snapshot = priceItem.GetKlineSnapshot();
            _logger.LogInformation("Klines in-memory for {Resolved}: {Count} candles", resolvedSymbol, snapshot.Count);
            if (snapshot.Any())
            {
                return Ok(snapshot.Select(k => new KlineDto
                {
                    OpenTime = k.OpenTime, Open = k.Open, High = k.High,
                    Low = k.Low, Close = k.Close, Volume = k.Volume
                }).ToList());
            }
        }
        else
        {
            _logger.LogWarning("Klines: {Resolved} not found in Prices dictionary. Available keys sample: {Keys}",
                resolvedSymbol, string.Join(", ", _state.Prices.Keys.Take(10)));
        }

        // Fallback to DB (try both resolved and original symbol)
        var klines = await _db.GetKlineAsync(resolvedSymbol);
        _logger.LogInformation("Klines DB fallback for {Resolved}: {Count} rows", resolvedSymbol, klines.Count);
        if (!klines.Any() && resolvedSymbol != symbol)
        {
            klines = await _db.GetKlineAsync(symbol);
            _logger.LogInformation("Klines DB fallback for {Symbol}: {Count} rows", symbol, klines.Count);
        }
        if (klines.Any())
        {
            return Ok(klines.Select(k => new KlineDto
            {
                OpenTime = k.OpenTime, Open = k.Open, High = k.High,
                Low = k.Low, Close = k.Close, Volume = k.Volume
            }).ToList());
        }

        // If all else fails, return empty
        return Ok(new List<KlineDto>());
    }

    private static KlineInterval? ParseInterval(string? interval)
    {
        return interval switch
        {
            "1" => KlineInterval.OneMinute,
            "5" => KlineInterval.FiveMinutes,
            "15" => KlineInterval.FifteenMinutes,
            "30" => KlineInterval.ThirtyMinutes,
            "60" => KlineInterval.OneHour,
            "240" => KlineInterval.FourHour,
            "1D" or null => KlineInterval.OneDay,
            "1W" => KlineInterval.OneWeek,
            _ => KlineInterval.OneDay,
        };
    }

    [HttpGet("{symbol}/klines-debug")]
    public async Task<ActionResult<object>> GetKlinesDebug(string symbol)
    {
        symbol = Uri.UnescapeDataString(symbol);
        var resolvedSymbol = _state.ResolveSymbolKey(symbol);
        var cleanSymbol = resolvedSymbol.Replace(".F/", "/").Replace(".B/", "/");
        var candidates = _state.GetApiPairCandidates(cleanSymbol);

        var pricesKey = _state.Prices.ContainsKey(resolvedSymbol);
        var snapshotCount = 0;
        if (_state.Prices.TryGetValue(resolvedSymbol, out var pi))
            snapshotCount = pi.GetKlineSnapshot().Count;

        var dbKlines = await _db.GetKlineAsync(resolvedSymbol);
        var dbKlinesOriginal = resolvedSymbol != symbol ? await _db.GetKlineAsync(symbol) : new List<DerivedKline>();

        // Try each candidate against Kraken API (just 1 candle to test)
        var apiResults = new Dictionary<string, int>();
        foreach (var c in candidates.Take(10))
        {
            try
            {
                var r = await _kraken.FetchKlinesInternalDirect(c, KlineInterval.OneDay, DateTime.UtcNow.AddDays(-3));
                apiResults[c] = r;
            }
            catch { apiResults[c] = -1; }
        }

        var cached = _state.ApiPairNameCache.TryGetValue(cleanSymbol, out var cachedVal) ? cachedVal : null;

        return Ok(new
        {
            inputSymbol = symbol,
            resolvedSymbol,
            cleanSymbol,
            pricesKeyExists = pricesKey,
            inMemoryKlines = snapshotCount,
            dbKlinesResolved = dbKlines.Count,
            dbKlinesOriginal = dbKlinesOriginal.Count,
            cachedApiName = cached,
            candidates,
            apiTestResults = apiResults,
            pricesKeysSample = _state.Prices.Keys.Take(15).ToList()
        });
    }

    private static DateTime GetSinceForInterval(KlineInterval interval)
    {
        return interval switch
        {
            KlineInterval.OneMinute => DateTime.UtcNow.AddHours(-12),
            KlineInterval.FiveMinutes => DateTime.UtcNow.AddDays(-3),
            KlineInterval.FifteenMinutes => DateTime.UtcNow.AddDays(-7),
            KlineInterval.ThirtyMinutes => DateTime.UtcNow.AddDays(-14),
            KlineInterval.OneHour => DateTime.UtcNow.AddDays(-30),
            KlineInterval.FourHour => DateTime.UtcNow.AddDays(-90),
            KlineInterval.OneWeek => DateTime.UtcNow.AddYears(-14),
            _ => DateTime.UtcNow.AddYears(-3),
        };
    }
}
