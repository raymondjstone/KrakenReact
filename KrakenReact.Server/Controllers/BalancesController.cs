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
    private static readonly HashSet<string> FiatAssets = new(StringComparer.OrdinalIgnoreCase)
        { "USD", "USDT", "USDC", "GBP", "EUR", "CAD", "AUD", "JPY", "CHF" };

    private readonly TradingStateService _state;
    private readonly KrakenDbContext _db;
    private readonly ILogger<BalancesController> _logger;

    public BalancesController(TradingStateService state, KrakenDbContext db, ILogger<BalancesController> logger)
    {
        _state = state;
        _db = db;
        _logger = logger;
    }

    [DevelopmentOnly]
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

            // Refresh price for any balance where the cached value is still 0
            // (happens at startup before klines arrive, or if V2 WS overwrote with 0)
            foreach (var balance in balances)
            {
                if (balance.LatestPrice == 0)
                {
                    var kline = _state.LatestPrice(balance.Asset);
                    if (kline != null && kline.Close > 0)
                    {
                        balance.LatestPrice = kline.Close;
                        balance.LatestValue = Math.Round(balance.Total * kline.Close, 2);
                        balance.LatestValueGbp = usdGbpRate > 0 ? Math.Round(balance.LatestValue * usdGbpRate, 2) : 0;
                    }
                }
            }

            // Cost basis / P&L come from TradingStateService.CostBasis, which is rebuilt whenever trades refresh
            // (chronological moving-average pool). This used to be recomputed here on every request by scanning
            // every trade for every balance — and it was the ONLY place the value was set, so the stop-loss /
            // take-profit jobs had no cost basis unless a browser had recently requested this endpoint.
            foreach (var balance in balances)
            {
                _state.ApplyCostBasis(balance);
                if (usdGbpRate > 0 && !FiatAssets.Contains(balance.Asset))
                    balance.LatestValueGbp = balance.LatestValue * usdGbpRate;
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

    /// <summary>GET /api/balances/rebalance?targets=BTC:40,ETH:30,USD:30 — drift vs target allocation</summary>
    [HttpGet("rebalance")]
    public ActionResult GetRebalance([FromQuery] string? targets)
    {
        if (string.IsNullOrWhiteSpace(targets))
            return Ok(new List<object>());

        var targetMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in targets.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 && decimal.TryParse(kv[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                targetMap[kv[0].Trim()] = pct;
        }

        var balances = _state.Balances.Values.Where(b => b.Total > 0 && b.LatestValue > 0).ToList();
        var totalUsd = balances.Sum(b => b.LatestValue);
        if (totalUsd <= 0) return Ok(new List<object>());

        var result = targetMap.Select(kvp =>
        {
            var asset = kvp.Key;
            var targetPct = kvp.Value;
            var bal = balances.FirstOrDefault(b => string.Equals(b.Asset, asset, StringComparison.OrdinalIgnoreCase));
            var currentUsd = bal?.LatestValue ?? 0m;
            var currentPct = totalUsd > 0 ? currentUsd / totalUsd * 100m : 0m;
            var driftPct = currentPct - targetPct;
            var targetUsd = totalUsd * targetPct / 100m;
            var diffUsd = targetUsd - currentUsd;
            var price = bal?.LatestPrice ?? 0m;
            var diffQty = price > 0 ? diffUsd / price : 0m;

            return new
            {
                asset,
                targetPct           = Math.Round(targetPct, 2),
                currentPct          = Math.Round(currentPct, 2),
                driftPct            = Math.Round(driftPct, 2),
                currentUsd          = Math.Round(currentUsd, 2),
                targetUsd           = Math.Round(targetUsd, 2),
                diffUsd             = Math.Round(diffUsd, 2),
                diffQty             = Math.Round(diffQty, 6),
                currentPrice        = price,
                action              = diffUsd > 0 ? "BUY" : diffUsd < 0 ? "SELL" : "HOLD",
            };
        }).OrderByDescending(r => Math.Abs(r.driftPct)).ToList();

        return Ok(new { totalUsd = Math.Round(totalUsd, 2), rows = result });
    }
}
