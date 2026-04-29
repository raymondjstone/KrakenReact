using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Kraken.Net.Enums;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TradesController : ControllerBase
{
    private readonly DbMethods _db;
    private readonly TradingStateService _state;

    public TradesController(DbMethods db, TradingStateService state)
    {
        _db = db;
        _state = state;
    }

    [HttpGet]
    public async Task<ActionResult<List<TradeDto>>> GetAll()
    {
        var trades = await _db.GetTradesAsync();
        var ledgers = await _db.GetLedgersAsync();

        return Ok(trades.Select(t =>
        {
            var linkedLedgers = ledgers.Where(l => l.ReferenceId == t.Id).ToList();
            return new TradeDto
            {
                Id = t.Id, OrderId = t.OrderId, Symbol = t.Symbol,
                Timestamp = t.Timestamp, Side = t.Side.ToString(), Type = t.Type.ToString(),
                Price = t.Price, Quantity = t.Quantity, QuoteQuantity = t.QuoteQuantity,
                Fee = t.Fee, Margin = t.Margin,
                NettTotal = t.Side == OrderSide.Buy ? t.QuoteQuantity + t.Fee : t.QuoteQuantity - t.Fee,
                PositionStatus = t.PositionStatus,
                ClosedQuantity = t.ClosedQuantity, ClosedProfitLoss = t.ClosedProfitLoss,
                ClosedAveragePrice = t.ClosedAveragePrice, ClosedCost = t.ClosedCost,
                ClosedFee = t.ClosedFee, ClosedMargin = t.ClosedMargin,
                LedgerItems = linkedLedgers.Select(l => new LedgerDto
                {
                    Id = l.Id, ReferenceId = l.ReferenceId, Timestamp = l.Timestamp,
                    Type = l.Type.ToString(), SubType = l.SubType, Asset = l.Asset,
                    Quantity = l.Quantity, Fee = l.Fee, BalanceAfter = l.BalanceAfter,
                    FeePercentage = l.BalanceAfter == 0 ? 0 : Math.Round(l.Fee / (l.BalanceAfter + l.Fee) * 100, 2),
                    AssetClass = l.AssetClass
                }).ToList()
            };
        }).ToList());
    }

    [HttpGet("grouped")]
    public async Task<ActionResult<List<TradeDto>>> GetGrouped()
    {
        var trades = await _db.GetTradesAsync();
        var ledgers = await _db.GetLedgersAsync();
        var grouped = trades.GroupBy(t => t.OrderId).Select(g =>
        {
            var first = g.First();
            var totalQty = g.Sum(x => x.Quantity);
            return new TradeDto
            {
                Id = g.Key, OrderId = g.Key, Symbol = first.Symbol,
                Timestamp = first.Timestamp, Side = first.Side.ToString(), Type = first.Type.ToString(),
                Price = totalQty == 0 ? 0 : g.Sum(o => o.Quantity * o.Price) / totalQty,
                Quantity = totalQty, QuoteQuantity = g.Sum(x => x.QuoteQuantity),
                Fee = g.Sum(o => o.Fee), Margin = g.Sum(o => o.Margin),
                NettTotal = first.Side == OrderSide.Buy ? g.Sum(x => x.QuoteQuantity) + g.Sum(o => o.Fee) : g.Sum(x => x.QuoteQuantity) - g.Sum(o => o.Fee),
                PositionStatus = first.PositionStatus,
                ClosedQuantity = g.Sum(x => x.ClosedQuantity),
                ClosedProfitLoss = g.Sum(x => x.ClosedProfitLoss),
                ClosedAveragePrice = totalQty == 0 ? 0 : g.Sum(x => x.Price * x.Quantity) / totalQty,
                ClosedCost = g.Sum(x => x.ClosedCost),
                ClosedFee = g.Sum(x => x.ClosedFee),
                ClosedMargin = g.Sum(x => x.ClosedMargin),
                // Include constituent trades for drill-down
                ConstituentTrades = g.Select(t => {
                    var linkedLedgers = ledgers.Where(l => l.ReferenceId == t.Id).ToList();
                    return new TradeDto
                    {
                        Id = t.Id, OrderId = t.OrderId, Symbol = t.Symbol,
                        Timestamp = t.Timestamp, Side = t.Side.ToString(), Type = t.Type.ToString(),
                        Price = t.Price, Quantity = t.Quantity, QuoteQuantity = t.QuoteQuantity,
                        Fee = t.Fee, Margin = t.Margin,
                        NettTotal = t.Side == OrderSide.Buy ? t.QuoteQuantity + t.Fee : t.QuoteQuantity - t.Fee,
                        ClosedProfitLoss = t.ClosedProfitLoss,
                        LedgerItems = linkedLedgers.Select(l => new LedgerDto
                        {
                            Id = l.Id, ReferenceId = l.ReferenceId, Timestamp = l.Timestamp,
                            Type = l.Type.ToString(), SubType = l.SubType, Asset = l.Asset,
                            Quantity = l.Quantity, Fee = l.Fee, BalanceAfter = l.BalanceAfter,
                            FeePercentage = l.BalanceAfter == 0 ? 0 : Math.Round(l.Fee / (l.BalanceAfter + l.Fee) * 100, 2),
                        }).ToList()
                    };
                }).ToList()
            };
        }).ToList();
        return Ok(grouped);
    }

    /// <summary>GET /api/trades/summary — realised P/L per symbol grouped by base asset</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        var trades = await _db.GetTradesAsync();
        var result = trades
            .GroupBy(t =>
            {
                var sym = t.Symbol ?? "";
                return sym.Contains('/') ? sym.Split('/')[0] : _state.NormalizeOrderSymbolBase(sym);
            })
            .Select(g =>
            {
                var buys = g.Where(t => t.Side == OrderSide.Buy).ToList();
                var sells = g.Where(t => t.Side == OrderSide.Sell).ToList();
                var totalBoughtQty = buys.Sum(t => t.Quantity);
                var totalSoldQty = sells.Sum(t => t.Quantity);
                var totalCost = buys.Sum(t => t.QuoteQuantity > 0 ? t.QuoteQuantity : t.Price * t.Quantity);
                var totalProceeds = sells.Sum(t => t.QuoteQuantity > 0 ? t.QuoteQuantity : t.Price * t.Quantity);
                var totalFees = g.Sum(t => t.Fee);
                var avgCost = totalBoughtQty > 0 ? totalCost / totalBoughtQty : 0m;
                var realisedPl = sells.Sum(t => t.ClosedProfitLoss ?? 0m);
                var lastTrade = g.Max(t => t.Timestamp);

                return new
                {
                    asset = g.Key,
                    tradeCount = g.Count(),
                    totalBoughtQty = Math.Round(totalBoughtQty, 6),
                    totalSoldQty = Math.Round(totalSoldQty, 6),
                    netQty = Math.Round(totalBoughtQty - totalSoldQty, 6),
                    totalCost = Math.Round(totalCost, 2),
                    totalProceeds = Math.Round(totalProceeds, 2),
                    totalFees = Math.Round(totalFees, 4),
                    avgCostPerUnit = Math.Round(avgCost, 6),
                    realisedPl = Math.Round(realisedPl, 2),
                    lastTrade,
                };
            })
            .OrderByDescending(s => s.lastTrade)
            .ToList();

        return Ok(result);
    }
}
