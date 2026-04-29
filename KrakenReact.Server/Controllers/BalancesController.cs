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
    public ActionResult GetAll()
    {
        try
        {
            var balances = _state.Balances.Values.ToList();
            var usdGbpRate = _state.GetUsdGbpRate();

            // Use cached snapshots from BackgroundTaskService — DB scans on every poll were timing out at 30s.
            var allTrades = _state.CachedTrades;
            var allLedgers = _state.CachedLedgers;

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

    /// <summary>GET /api/balances/period-pl — P/L for each held asset over 1d, 7d, 30d</summary>
    [HttpGet("period-pl")]
    public ActionResult GetPeriodPl()
    {
        var result = new List<object>();
        var balances = _state.Balances.Values.Where(b => b.Total > 0 && b.LatestPrice > 0).ToList();
        var fiat = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "USD", "USDT", "USDC", "GBP", "EUR", "CAD", "AUD", "JPY", "CHF" };

        foreach (var bal in balances)
        {
            if (fiat.Contains(bal.Asset)) continue;
            if (!_state.Prices.TryGetValue(bal.Asset + "/USD", out var priceItem))
            {
                // Try via symbols lookup
                priceItem = _state.Prices.Values.FirstOrDefault(p =>
                    TradingStateService.NormalizeAsset(p.Base) == bal.Asset && p.CCY is "USD" or "ZUSD");
            }
            if (priceItem == null) continue;

            var klines = priceItem.GetKlineSnapshot()
                .Where(k => k.Interval == "OneDay")
                .OrderByDescending(k => k.OpenTime)
                .ToList();

            decimal? price1d = klines.Skip(1).FirstOrDefault()?.Close;
            decimal? price7d = klines.Skip(7).FirstOrDefault()?.Close;
            decimal? price30d = klines.Skip(30).FirstOrDefault()?.Close;
            var current = bal.LatestPrice;

            result.Add(new
            {
                asset  = bal.Asset,
                pl1d   = price1d > 0 ? Math.Round((current - price1d.Value) / price1d.Value * 100, 2) : (decimal?)null,
                pl7d   = price7d > 0 ? Math.Round((current - price7d.Value) / price7d.Value * 100, 2) : (decimal?)null,
                pl30d  = price30d > 0 ? Math.Round((current - price30d.Value) / price30d.Value * 100, 2) : (decimal?)null,
            });
        }

        return Ok(result);
    }

    /// <summary>GET /api/balances/atr — 14-day ATR as % of price for each held non-fiat asset</summary>
    [HttpGet("atr")]
    public ActionResult GetAtr()
    {
        var fiat = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "USD", "USDT", "USDC", "GBP", "EUR", "CAD", "AUD", "JPY", "CHF" };
        var result = new List<object>();

        foreach (var bal in _state.Balances.Values.Where(b => b.Total > 0 && b.LatestPrice > 0))
        {
            if (fiat.Contains(bal.Asset)) continue;

            // Resolve price item
            PriceDataItem? priceItem = null;
            if (!_state.Prices.TryGetValue(bal.Asset + "/USD", out priceItem))
                priceItem = _state.Prices.Values.FirstOrDefault(p =>
                    TradingStateService.NormalizeAsset(p.Base) == bal.Asset && p.CCY is "USD" or "ZUSD");
            if (priceItem == null) continue;

            var klines = priceItem.GetKlineSnapshot()
                .Where(k => k.Interval == "OneDay")
                .OrderBy(k => k.OpenTime)
                .TakeLast(30)
                .ToList();

            if (klines.Count < 2) continue;

            // 14-period ATR
            const int period = 14;
            var trValues = new List<decimal>();
            for (int i = 1; i < klines.Count; i++)
            {
                var high = klines[i].High;
                var low = klines[i].Low;
                var prevClose = klines[i - 1].Close;
                var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                trValues.Add(tr);
            }
            var atrValues = trValues.TakeLast(period).ToList();
            var atr = atrValues.Count > 0 ? atrValues.Average() : 0m;
            var atrPct = bal.LatestPrice > 0 ? Math.Round(atr / bal.LatestPrice * 100, 2) : 0m;

            result.Add(new { asset = bal.Asset, atr = Math.Round(atr, 6), atrPct });
        }

        return Ok(result);
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
