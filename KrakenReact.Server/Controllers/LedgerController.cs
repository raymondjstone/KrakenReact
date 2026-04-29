using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Kraken.Net.Enums;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LedgerController : ControllerBase
{
    private readonly DbMethods _db;
    private readonly TradingStateService _state;

    public LedgerController(DbMethods db, TradingStateService state)
    {
        _db = db;
        _state = state;
    }

    [HttpGet]
    public async Task<ActionResult<List<LedgerDto>>> GetAll()
    {
        var ledgers = await _db.GetLedgersAsync();
        return Ok(ledgers.Select(l => new LedgerDto
        {
            Id = l.Id, ReferenceId = l.ReferenceId, Timestamp = l.Timestamp,
            Type = l.Type.ToString(), SubType = l.SubType, Asset = l.Asset,
            Quantity = l.Quantity, Fee = l.Fee, BalanceAfter = l.BalanceAfter,
            FeePercentage = l.BalanceAfter == 0 ? 0 : Math.Round(l.Fee / (l.BalanceAfter + l.Fee) * 100, 2),
            AssetClass = l.AssetClass
        }).ToList());
    }

    /// <summary>GET /api/ledger/staking — staking rewards summary per asset</summary>
    [HttpGet("staking")]
    public ActionResult GetStaking()
    {
        var rewards = _state.CachedLedgers
            .Where(l =>
                l.Type == LedgerEntryType.Staking &&
                l.Quantity > 0 &&
                l.SubType != "spotFromStaking" &&
                l.SubType != "spotToStaking")
            .ToList();

        if (!rewards.Any()) return Ok(new List<object>());

        var usdGbpRate = _state.GetUsdGbpRate();

        var grouped = rewards
            .GroupBy(l => TradingStateService.NormalizeAsset(l.Asset))
            .Select(g =>
            {
                var asset = g.Key;
                var entries = g.OrderBy(l => l.Timestamp).ToList();
                var totalQty = entries.Sum(l => l.Quantity);
                var currentPrice = 0m;
                var kline = _state.LatestPrice(asset);
                if (kline != null) currentPrice = kline.Close;

                var totalUsd = totalQty * currentPrice;
                var totalGbp = usdGbpRate > 0 ? totalUsd * usdGbpRate : 0m;

                // Estimate APY: total earned / average balance over period * annualisation factor
                decimal estimatedApy = 0m;
                if (entries.Count >= 2)
                {
                    var periodDays = (entries.Last().Timestamp - entries.First().Timestamp).TotalDays;
                    if (periodDays > 0)
                    {
                        var bal = _state.Balances.TryGetValue(asset, out var b) ? b.Total : totalQty;
                        if (bal > 0)
                            estimatedApy = Math.Round((decimal)(totalQty / bal) / (decimal)periodDays * 365m * 100m, 2);
                    }
                }

                // Projected annual income at current price
                var projectedAnnualUsd = estimatedApy > 0 && currentPrice > 0
                    ? Math.Round((_state.Balances.TryGetValue(asset, out var bal2) ? bal2.Total : totalQty) * (estimatedApy / 100m) * currentPrice, 2)
                    : 0m;

                var recent30d = entries.Where(l => l.Timestamp >= DateTime.UtcNow.AddDays(-30)).Sum(l => l.Quantity);
                var recent7d  = entries.Where(l => l.Timestamp >= DateTime.UtcNow.AddDays(-7)).Sum(l => l.Quantity);

                return new
                {
                    asset,
                    totalQty        = Math.Round(totalQty, 8),
                    currentPrice    = Math.Round(currentPrice, 6),
                    totalUsd        = Math.Round(totalUsd, 2),
                    totalGbp        = Math.Round(totalGbp, 2),
                    estimatedApy,
                    projectedAnnualUsd,
                    rewardCount     = entries.Count,
                    firstRewardAt   = entries.First().Timestamp,
                    lastRewardAt    = entries.Last().Timestamp,
                    recent7dQty     = Math.Round(recent7d, 8),
                    recent30dQty    = Math.Round(recent30d, 8),
                };
            })
            .OrderByDescending(r => r.totalUsd)
            .ToList();

        return Ok(grouped);
    }
}
