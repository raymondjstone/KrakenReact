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

    [HttpGet("diagnostics")]
    public async Task<ActionResult> GetDiagnostics()
    {
        var tradeCount = await _db.Trades.CountAsync();
        var sampleTrades = await _db.Trades.Take(5).Select(t => new { t.Symbol, t.Side, t.Price, t.Quantity }).ToListAsync();
        var balances = _state.Balances.Values.Where(b => b.Total > 0).Select(b => new { b.Asset, b.Total }).Take(5).ToList();

        return Ok(new
        {
            TradeCount = tradeCount,
            SampleTrades = sampleTrades,
            SampleBalances = balances
        });
    }

    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        try
        {
            var balances = _state.Balances.Values.ToList();
            var usdGbpRate = _state.GetUsdGbpRate();

            // Load all trades and ledgers once for P/L calculation (client-side filtering since NormalizeAsset can't translate to SQL)
            var allTrades = await _db.Trades.AsNoTracking().ToListAsync();
            var allLedgers = await _db.Ledgers.AsNoTracking().ToListAsync();

            // DIAGNOSTIC: Return diagnostic info showing first balance with trades
            var firstBalanceWithTrades = balances.FirstOrDefault(b => b.Total > 0 && b.TotalCostBasis > 0);
            var diagnosticInfo = new
            {
                TotalTradesInDb = allTrades.Count,
                TotalLedgersInDb = allLedgers.Count,
                SampleTradeSymbols = allTrades.Take(3).Select(t => t.Symbol).ToList(),
                SampleBalanceAssets = balances.Where(b => b.Total > 0).Take(3).Select(b => b.Asset).ToList(),
                SampleCalculation = firstBalanceWithTrades == null ? null : new
                {
                    Asset = firstBalanceWithTrades.Asset,
                    TotalCostBasis = firstBalanceWithTrades.TotalCostBasis,
                    TotalFees = firstBalanceWithTrades.TotalFees,
                    CurrentValue = firstBalanceWithTrades.LatestValue,
                    NetProfitLoss = firstBalanceWithTrades.NetProfitLoss,
                    NetProfitLossPercentage = firstBalanceWithTrades.NetProfitLossPercentage
                }
            };

            // Calculate profit/loss for each balance
            foreach (var balance in balances)
            {
                // Skip P/L calculation for fiat currencies (they don't have a "cost basis")
                var fiatCurrencies = new[] { "USD", "USDT", "USDC", "GBP", "EUR", "CAD", "AUD", "JPY", "CHF" };
                if (fiatCurrencies.Contains(balance.Asset))
                {
                    // Leave cost basis and P/L fields as null/zero
                    continue;
                }

                var normalizedAsset = TradingStateService.NormalizeAsset(balance.Asset);

                // Match trades where the base asset (first part of the pair) matches this balance's asset
                // Note: KrakenUserTrade might have either 'Symbol' or 'Pair' property depending on library version
                var trades = allTrades.Where(t =>
                {
                    var pairName = t.Symbol ?? "";
                    if (string.IsNullOrEmpty(pairName)) return false;

                    // Use NormalizeOrderSymbolBase to reliably extract and normalize the base asset
                    // This handles all Kraken symbol formats: "XBT/USD", "XBTUSD", "XXBTZUSD", etc.
                    var normalizedBaseAsset = _state.NormalizeOrderSymbolBase(pairName);

                    return normalizedBaseAsset == balance.Asset ||
                           normalizedBaseAsset == normalizedAsset;
                }).ToList();

                // Get ledger entries for this asset (staking, deposits, etc.)
                var ledgers = allLedgers.Where(l =>
                    l.Asset == balance.Asset ||
                    l.Asset == normalizedAsset ||
                    TradingStateService.NormalizeAsset(l.Asset) == normalizedAsset
                ).ToList();

                if (trades.Any() || ledgers.Any())
                {
                    var buyTrades = trades.Where(t => t.Side == Kraken.Net.Enums.OrderSide.Buy).ToList();
                    var sellTrades = trades.Where(t => t.Side == Kraken.Net.Enums.OrderSide.Sell).ToList();

                    // Calculate total bought and total sold (in base asset quantity)
                    var totalBought = buyTrades.Sum(t => t.Quantity);
                    var totalSold = sellTrades.Sum(t => t.Quantity);
                    var currentHolding = balance.Total; // Current balance from account

                    // Calculate total cost of all buys - convert different currencies to USD
                    decimal totalBuyCostUsd = 0;
                    foreach (var trade in buyTrades)
                    {
                        var quoteCurrency = GetQuoteCurrency(trade.Symbol);
                        var tradeCost = trade.QuoteQuantity > 0 ? trade.QuoteQuantity : (trade.Price * trade.Quantity);

                        // Convert to USD if needed
                        if (quoteCurrency == "USD" || string.IsNullOrEmpty(quoteCurrency))
                        {
                            totalBuyCostUsd += tradeCost;
                        }
                        else if (quoteCurrency == "GBP")
                        {
                            // Convert GBP to USD using current rate (approximation)
                            var gbpUsdRate = usdGbpRate > 0 ? (1 / usdGbpRate) : 1.27m;
                            totalBuyCostUsd += tradeCost * gbpUsdRate;
                        }
                        else if (quoteCurrency == "USDT" || quoteCurrency == "USDC")
                        {
                            // Assume 1:1 with USD
                            totalBuyCostUsd += tradeCost;
                        }
                        else if (quoteCurrency == "EUR")
                        {
                            // Approximate EUR to USD conversion
                            totalBuyCostUsd += tradeCost * 1.08m;
                        }
                        else
                        {
                            // Unknown currency, use as-is (assume USD)
                            totalBuyCostUsd += tradeCost;
                        }
                    }

                    // Calculate fees from all trades
                    balance.TotalFees = trades.Sum(t => t.Fee);

                    // If we've sold some, calculate the proportional cost basis for remaining holdings
                    if (totalBought > 0)
                    {
                        if (totalSold > 0 && currentHolding > 0)
                        {
                            // Average cost per unit = total spent / total bought
                            var avgCostPerUnit = totalBuyCostUsd / totalBought;
                            // Cost basis for current holdings = avg cost * current quantity
                            balance.TotalCostBasis = avgCostPerUnit * currentHolding;
                        }
                        else
                        {
                            // No sells, so cost basis is just total buy cost
                            balance.TotalCostBasis = totalBuyCostUsd;
                        }
                    }

                    // Add staking rewards to cost basis (they represent additional "cost" of acquiring the asset)
                    // IMPORTANT: Only include actual staking REWARDS (Type == Staking), NOT transfers in/out of staking
                    // Transfers are just moving existing assets between available/staked balances (not new acquisitions)
                    var stakingRewards = ledgers.Where(l => 
                        l.Type == Kraken.Net.Enums.LedgerEntryType.Staking && 
                        l.Quantity > 0 &&
                        l.SubType != "spotFromStaking" &&  // Exclude unstaking transfers
                        l.SubType != "spotToStaking"       // Exclude staking transfers
                    ).ToList();
                    if (stakingRewards.Any())
                    {
                        // For staking rewards, we need to estimate their USD value at time of receipt
                        // Since we don't store historical prices, we'll use a conservative approach:
                        // Assume staking rewards have zero cost basis (they were "free")
                        // This makes profit calculations more accurate by not inflating the cost
                        // Alternative: Could multiply quantity by current price as a rough estimate
                        // balance.TotalCostBasis += stakingRewards.Sum(s => s.Quantity * balance.LatestPrice);
                    }

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

            // Recalculate portfolio percentages
            var totalPortfolioValue = balances.Sum(b => b.LatestValue);
            if (totalPortfolioValue > 0)
            {
                foreach (var b in balances)
                    b.PortfolioPercentage = Math.Round(b.LatestValue / totalPortfolioValue * 100, 2);
            }

            var hideAlmostZeroBalances = _state.HideAlmostZeroBalances;

            return Ok(new { balances, usdGbpRate, hideAlmostZeroBalances });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting balances");
            return StatusCode(500, "Error loading balances");
        }
    }

    private static string GetQuoteCurrency(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return "USD";

        // Symbol format is usually "BASE/QUOTE" like "XBT/USD" or "ETH/GBP"
        if (symbol.Contains('/'))
        {
            var parts = symbol.Split('/');
            return parts.Length > 1 ? parts[1] : "USD";
        }

        // If no slash, try to extract (e.g., "XBTUSD" -> "USD")
        // Common quote currencies are usually at the end
        if (symbol.EndsWith("USD")) return "USD";
        if (symbol.EndsWith("USDT")) return "USDT";
        if (symbol.EndsWith("USDC")) return "USDC";
        if (symbol.EndsWith("GBP")) return "GBP";
        if (symbol.EndsWith("EUR")) return "EUR";

        return "USD"; // Default assumption
    }
}
