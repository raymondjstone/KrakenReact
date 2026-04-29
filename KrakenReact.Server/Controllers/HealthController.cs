using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;

    public HealthController(KrakenDbContext db, TradingStateService state)
    {
        _db = db;
        _state = state;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var checks = new List<object>();

        // 1. Database connectivity
        bool dbOk = false;
        string dbMsg = "";
        try
        {
            var count = await _db.AppSettings.CountAsync();
            dbOk = true;
            dbMsg = $"{count} settings rows";
        }
        catch (Exception ex) { dbMsg = ex.Message; }
        checks.Add(new { name = "Database", ok = dbOk, detail = dbMsg });

        // 2. Symbols loaded
        var symbolCount = _state.Symbols.Count;
        checks.Add(new { name = "Symbols", ok = symbolCount > 0, detail = $"{symbolCount} symbols loaded" });

        // 3. Price data freshness — at least one price updated within last 10 minutes
        var recentPrice = _state.Prices.Values
            .Where(p => p.KrakenNewPricesLoadedTime > DateTime.MinValue)
            .OrderByDescending(p => p.KrakenNewPricesLoadedTime)
            .FirstOrDefault();
        var priceAge = recentPrice != null
            ? (DateTime.UtcNow - recentPrice.KrakenNewPricesLoadedTime).TotalMinutes
            : double.MaxValue;
        var priceOk = priceAge < 10;
        checks.Add(new
        {
            name = "Live Prices",
            ok = priceOk,
            detail = priceOk
                ? $"Last price {priceAge:F1} min ago ({recentPrice?.Symbol})"
                : recentPrice == null ? "No prices received" : $"Stale — last price {priceAge:F1} min ago"
        });

        // 4. Balances loaded
        var balCount = _state.Balances.Count;
        checks.Add(new { name = "Balances", ok = balCount > 0, detail = $"{balCount} balance entries" });

        // 5. ML predictions freshness — check DB for recent PredictionResults
        bool predOk = false;
        string predMsg = "";
        try
        {
            var latestPred = await _db.PredictionResults.AsNoTracking()
                .OrderByDescending(p => p.ComputedAt)
                .Select(p => new { p.Symbol, p.ComputedAt })
                .FirstOrDefaultAsync();
            if (latestPred == null)
            {
                predMsg = "No predictions found";
            }
            else
            {
                var ageH = (DateTime.UtcNow - latestPred.ComputedAt).TotalHours;
                predOk = ageH < 26;
                predMsg = $"{latestPred.Symbol} {ageH:F1}h ago";
            }
        }
        catch (Exception ex) { predMsg = ex.Message; }
        checks.Add(new { name = "ML Predictions", ok = predOk, detail = predMsg });

        // 6. Portfolio snapshots — latest snapshot within 26 hours
        bool snapOk = false;
        string snapMsg = "";
        try
        {
            var latest = await _db.PortfolioSnapshots.AsNoTracking()
                .OrderByDescending(s => s.Date)
                .Select(s => new { s.Date, s.TotalUsd })
                .FirstOrDefaultAsync();
            if (latest == null)
            {
                snapMsg = "No snapshots found";
            }
            else
            {
                var ageH = (DateTime.UtcNow.Date - latest.Date).TotalHours;
                snapOk = ageH < 26;
                snapMsg = $"${latest.TotalUsd:N0} on {latest.Date:yyyy-MM-dd} ({ageH:F0}h ago)";
            }
        }
        catch (Exception ex) { snapMsg = ex.Message; }
        checks.Add(new { name = "Portfolio Snapshot", ok = snapOk, detail = snapMsg });

        // 7. Initial data load complete
        checks.Add(new
        {
            name = "Initial Load",
            ok = !_state.InitialDataLoad,
            detail = _state.InitialDataLoad ? "Still loading…" : "Complete"
        });

        var allOk = checks.Cast<dynamic>().All(c => (bool)c.ok);
        return Ok(new
        {
            ok = allOk,
            checkedAt = DateTime.UtcNow,
            checks
        });
    }
}
