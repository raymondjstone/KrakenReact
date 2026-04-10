using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PricesController : ControllerBase
{
    private readonly TradingStateService _state;
    private readonly DbMethods _db;

    public PricesController(TradingStateService state, DbMethods db)
    {
        _state = state;
        _db = db;
    }

    [HttpGet]
    public ActionResult<List<PriceDto>> GetAll()
    {
        var prices = _state.GetPriceSnapshot().Select(p =>
        {
            var latest = p.LatestKline;
            var orders = _state.Orders.Values.Where(o => o.Symbol.StartsWith(p.SymbolNoSlash) && o.Status == "Closed" && o.Side == "Buy").ToList();
            var avgBuyPrice = orders.Any() ? Math.Round(orders.Sum(o => o.AveragePrice) / orders.Count, 9) : 0m;

            return new PriceDto
            {
                Symbol = p.Symbol,
                Base = p.Base,
                CCY = p.CCY,
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
    public async Task<ActionResult<List<KlineDto>>> GetKlines(string symbol)
    {
        // symbol comes URL-encoded, e.g. "SOL%2FUSD" -> "SOL/USD"
        symbol = Uri.UnescapeDataString(symbol);

        // Try in-memory first
        if (_state.Prices.TryGetValue(symbol, out var priceItem))
        {
            var snapshot = priceItem.GetKlineSnapshot();
            if (snapshot.Any())
            {
                return Ok(snapshot.Select(k => new KlineDto
                {
                    OpenTime = k.OpenTime, Open = k.Open, High = k.High,
                    Low = k.Low, Close = k.Close, Volume = k.Volume
                }).ToList());
            }
        }

        // Fallback to DB
        var klines = await _db.GetKlineAsync(symbol);
        return Ok(klines.Select(k => new KlineDto
        {
            OpenTime = k.OpenTime, Open = k.Open, High = k.High,
            Low = k.Low, Close = k.Close, Volume = k.Volume
        }).ToList());
    }
}
