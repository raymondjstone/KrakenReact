using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
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

    /// <summary>
    /// Checks a create/update request and works out the normalized direction and auto-order side. The direction match is
    /// case-insensitive ("Below" used to become "above", inverting the alert); an unrecognised direction still defaults to above.
    /// When an auto-order is switched on its side must be exactly Buy or Sell (an unrecognised side used to become a BUY), its
    /// quantity positive, and its price offset sane (-100% priced the order at zero).
    /// </summary>
    internal static string? Validate(CreatePriceAlertRequest req, out string direction, out string side)
    {
        direction = string.Equals(req.Direction?.Trim(), "below", StringComparison.OrdinalIgnoreCase) ? "below" : "above";
        side = InputRules.NormalizeSide(req.AutoOrderSide) ?? "Buy";

        if (string.IsNullOrWhiteSpace(req.Symbol) || req.TargetPrice <= 0)
            return "Symbol and positive target price required";
        if (req.AutoOrderQty < 0) return "AutoOrderQty cannot be negative";

        if (req.AutoOrderEnabled)
        {
            if (InputRules.NormalizeSide(req.AutoOrderSide) == null) return "The auto-order side must be Buy or Sell";
            if (req.AutoOrderQty <= 0) return "The auto-order quantity must be positive";
            if (req.AutoOrderOffsetPct is < -50m or > 50m) return "The auto-order price offset must be between -50% and 50%";
        }
        return null;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePriceAlertRequest req)
    {
        var problem = Validate(req, out var direction, out var side);
        if (problem != null) return BadRequest(new { message = problem });

        var alert = new PriceAlert
        {
            Symbol = req.Symbol.Trim(),
            TargetPrice = req.TargetPrice,
            Direction = direction,
            Note = req.Note ?? "",
            Active = true,
            CreatedAt = DateTime.UtcNow,
            AutoOrderEnabled = req.AutoOrderEnabled,
            AutoOrderSide = side,
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
        var problem = Validate(req, out var direction, out var side);
        if (problem != null) return BadRequest(new { message = problem });

        var alert = await _db.PriceAlerts.FindAsync(id);
        if (alert == null) return NotFound();

        alert.Symbol = req.Symbol.Trim();
        alert.TargetPrice = req.TargetPrice;
        alert.Direction = direction;
        alert.Note = req.Note ?? "";
        alert.AutoOrderEnabled = req.AutoOrderEnabled;
        alert.AutoOrderSide = side;
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
