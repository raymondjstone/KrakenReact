using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using KrakenReact.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BalancesController : ControllerBase
{
    private readonly TradingStateService _state;
    private readonly KrakenDbContext _db;
    private readonly ILogger<BalancesController> _logger;

    public BalancesController(TradingStateService state, KrakenDbContext db, ILogger<BalancesController> logger)
    {
        _state = state;
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        try
        {
            var balances = _state.Balances.Values.ToList();
            var usdGbpRate = _state.GetUsdGbpRate();

            // Calculate profit/loss for each balance
            foreach (var balance in balances)
            {
                var normalizedAsset = TradingStateService.NormalizeAsset(balance.Asset);

                // Get all trades for this asset
                var trades = await _db.Trades
                    .Where(t => t.Symbol == balance.Asset || 
                                t.Symbol == normalizedAsset ||
                                t.Symbol.Replace("/", "") == normalizedAsset ||
                                TradingStateService.NormalizeAsset(t.Symbol) == normalizedAsset)
                    .ToListAsync();

                if (trades.Any())
                {
                    // Calculate total cost basis and fees from buy orders
                    var buyTrades = trades.Where(t => t.Side == Kraken.Net.Enums.OrderSide.Buy).ToList();
                    balance.TotalCostBasis = buyTrades.Sum(t => t.Price * t.Quantity);
                    balance.TotalFees = trades.Sum(t => t.Fee);

                    // Net profit/loss = current value - cost basis - fees
                    if (balance.TotalCostBasis > 0)
                    {
                        balance.NetProfitLoss = balance.LatestValue - balance.TotalCostBasis - balance.TotalFees;
                        balance.NetProfitLossPercentage = (balance.NetProfitLoss / balance.TotalCostBasis) * 100m;
                    }
                }

                // Add GBP value
                if (usdGbpRate > 0)
                {
                    balance.LatestValueGbp = balance.LatestValue * usdGbpRate;
                }
            }

            return Ok(new { balances, usdGbpRate });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting balances");
            return StatusCode(500, "Error loading balances");
        }
    }
}
