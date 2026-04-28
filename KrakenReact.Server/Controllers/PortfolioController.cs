using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/portfolio")]
public class PortfolioController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;

    public PortfolioController(KrakenDbContext db, TradingStateService state)
    {
        _db = db;
        _state = state;
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.Date.AddDays(-days);
        var snapshots = await _db.PortfolioSnapshots
            .Where(s => s.Date >= since)
            .OrderBy(s => s.Date)
            .Select(s => new { date = s.Date, totalUsd = s.TotalUsd, totalGbp = s.TotalGbp })
            .ToListAsync();
        return Ok(snapshots);
    }

    /// <summary>Manually trigger a portfolio snapshot (useful for testing)</summary>
    [HttpPost("snapshot")]
    public async Task<IActionResult> TakeSnapshot()
    {
        var totalUsd = _state.Balances.Values.Sum(b => b.LatestValue);
        var totalGbp = _state.Balances.Values.Sum(b => b.LatestValueGbp);
        var today = DateTime.UtcNow.Date;

        var existing = await _db.PortfolioSnapshots.FindAsync(today);
        if (existing != null)
        {
            existing.TotalUsd = totalUsd;
            existing.TotalGbp = totalGbp;
        }
        else
        {
            _db.PortfolioSnapshots.Add(new PortfolioSnapshot
            {
                Date = today,
                TotalUsd = totalUsd,
                TotalGbp = totalGbp,
            });
        }
        await _db.SaveChangesAsync();
        return Ok(new { date = today, totalUsd, totalGbp });
    }
}
