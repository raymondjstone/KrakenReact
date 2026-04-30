using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/pricealerts")]
public class PriceAlertsController : ControllerBase
{
    private readonly KrakenDbContext _db;

    public PriceAlertsController(KrakenDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var alerts = await _db.PriceAlerts
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
        return Ok(alerts);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePriceAlertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Symbol) || req.TargetPrice <= 0)
            return BadRequest(new { message = "Symbol and positive target price required" });

        var alert = new PriceAlert
        {
            Symbol = req.Symbol.Trim(),
            TargetPrice = req.TargetPrice,
            Direction = req.Direction == "below" ? "below" : "above",
            Note = req.Note ?? "",
            Active = true,
            CreatedAt = DateTime.UtcNow,
            AutoOrderEnabled = req.AutoOrderEnabled,
            AutoOrderSide = req.AutoOrderSide == "Sell" ? "Sell" : "Buy",
            AutoOrderQty = req.AutoOrderQty,
            AutoOrderOffsetPct = req.AutoOrderOffsetPct,
        };
        _db.PriceAlerts.Add(alert);
        await _db.SaveChangesAsync();
        return Ok(alert);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] CreatePriceAlertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Symbol) || req.TargetPrice <= 0)
            return BadRequest(new { message = "Symbol and positive target price required" });

        var alert = await _db.PriceAlerts.FindAsync(id);
        if (alert == null) return NotFound();

        alert.Symbol = req.Symbol.Trim();
        alert.TargetPrice = req.TargetPrice;
        alert.Direction = req.Direction == "below" ? "below" : "above";
        alert.Note = req.Note ?? "";
        alert.AutoOrderEnabled = req.AutoOrderEnabled;
        alert.AutoOrderSide = req.AutoOrderSide == "Sell" ? "Sell" : "Buy";
        alert.AutoOrderQty = req.AutoOrderQty;
        alert.AutoOrderOffsetPct = req.AutoOrderOffsetPct;

        await _db.SaveChangesAsync();
        return Ok(alert);
    }

    [HttpPost("{id}/reset")]
    public async Task<IActionResult> Reset(int id)
    {
        var alert = await _db.PriceAlerts.FindAsync(id);
        if (alert == null) return NotFound();

        alert.Active = true;
        alert.TriggeredAt = null;
        await _db.SaveChangesAsync();
        return Ok(alert);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var alert = await _db.PriceAlerts.FindAsync(id);
        if (alert == null) return NotFound();
        _db.PriceAlerts.Remove(alert);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreatePriceAlertRequest(
    string Symbol,
    decimal TargetPrice,
    string Direction,
    string? Note,
    bool AutoOrderEnabled = false,
    string AutoOrderSide = "Buy",
    decimal AutoOrderQty = 0,
    decimal AutoOrderOffsetPct = 0);
