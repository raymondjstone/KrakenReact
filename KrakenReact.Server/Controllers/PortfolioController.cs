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

    /// <summary>GET /api/portfolio/metrics — Sharpe ratio and max drawdown from snapshot history</summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetrics([FromQuery] int days = 365)
    {
        days = Math.Clamp(days, 14, 730);
        var since = DateTime.UtcNow.Date.AddDays(-days);
        var snapshots = await _db.PortfolioSnapshots
            .Where(s => s.Date >= since)
            .OrderBy(s => s.Date)
            .Select(s => new { s.Date, s.TotalUsd })
            .ToListAsync();

        if (snapshots.Count < 5)
            return Ok(new { sharpe = (double?)null, maxDrawdownPct = (double?)null, annualReturnPct = (double?)null, sampleDays = snapshots.Count });

        var m = PortfolioMetrics.Compute(snapshots.Select(s => (s.Date, (double)s.TotalUsd)).ToList());

        return Ok(new
        {
            sharpe = m.Sharpe,
            maxDrawdownPct = m.MaxDrawdownPct,
            annualReturnPct = m.AnnualReturnPct,   // null until the history spans enough days to annualise honestly
            sampleDays = m.SampleDays,
            spanDays = m.SpanDays,
        });
    }

    /// <summary>GET /api/portfolio/rolling-pnl — cumulative P&amp;L relative to first snapshot in window</summary>
    [HttpGet("rolling-pnl")]
    public async Task<IActionResult> GetRollingPnl([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.Date.AddDays(-days);
        var snapshots = await _db.PortfolioSnapshots
            .Where(s => s.Date >= since)
            .OrderBy(s => s.Date)
            .Select(s => new { s.Date, s.TotalUsd })
            .ToListAsync();

        if (snapshots.Count == 0) return Ok(Array.Empty<object>());

        var baseline = (double)snapshots[0].TotalUsd;
        var result = snapshots.Select(s => new
        {
            date = s.Date,
            pnlUsd = Math.Round((double)s.TotalUsd - baseline, 2),
            pnlPct = baseline > 0 ? Math.Round(((double)s.TotalUsd - baseline) / baseline * 100, 3) : 0.0,
            totalUsd = (double)s.TotalUsd,
        }).ToList();

        return Ok(result);
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
