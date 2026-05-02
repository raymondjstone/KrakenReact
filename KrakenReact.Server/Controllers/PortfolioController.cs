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

        var values = snapshots.Select(s => (double)s.TotalUsd).ToArray();

        // Daily returns
        var returns = new double[values.Length - 1];
        for (int i = 1; i < values.Length; i++)
            returns[i - 1] = values[i - 1] > 0 ? (values[i] - values[i - 1]) / values[i - 1] : 0;

        var mean = returns.Average();
        var variance = returns.Select(r => (r - mean) * (r - mean)).Average();
        var stdDev = Math.Sqrt(variance);
        var sharpe = stdDev > 0 ? Math.Round(mean / stdDev * Math.Sqrt(252), 3) : 0;

        // Max drawdown
        double peak = values[0], maxDd = 0;
        foreach (var v in values)
        {
            if (v > peak) peak = v;
            if (peak > 0)
            {
                var dd = (peak - v) / peak;
                if (dd > maxDd) maxDd = dd;
            }
        }

        // Annualised return
        var totalReturn = values[0] > 0 ? (values[^1] - values[0]) / values[0] : 0;
        var annualReturn = snapshots.Count > 1
            ? Math.Pow(1 + totalReturn, 365.0 / snapshots.Count) - 1
            : 0;

        return Ok(new
        {
            sharpe = Math.Round(sharpe, 3),
            maxDrawdownPct = Math.Round(maxDd * 100, 2),
            annualReturnPct = Math.Round(annualReturn * 100, 2),
            sampleDays = snapshots.Count,
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
