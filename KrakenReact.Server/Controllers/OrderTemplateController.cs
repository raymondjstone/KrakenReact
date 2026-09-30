using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/ordertemplates")]
public class OrderTemplateController : ControllerBase
{
    private readonly KrakenDbContext _db;

    public OrderTemplateController(KrakenDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.OrderTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync());

    /// <summary>Rules for a template, for Create and Update alike (Update used to accept an empty name, an unknown side and negative
    /// quantities). Normalizes the side in place.</summary>
    internal static string? ValidateAndNormalize(OrderTemplate t)
    {
        if (string.IsNullOrWhiteSpace(t.Name)) return "Name required";
        if (string.IsNullOrWhiteSpace(t.Symbol)) return "Symbol required";
        var side = InputRules.NormalizeSide(t.Side);
        if (side == null) return "Side must be Buy or Sell";
        if (t.Quantity is <= 0) return "Quantity must be positive when given";
        if (t.QtyPct is <= 0 or > 100) return "QtyPct must be greater than 0 and at most 100 when given";
        if (t.PriceOffsetPct is < -100m or > 1000m) return "PriceOffsetPct is out of range";
        t.Name = t.Name.Trim();
        t.Symbol = t.Symbol.Trim();
        t.Side = side;
        return null;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] OrderTemplate template)
    {
        var problem = ValidateAndNormalize(template);
        if (problem != null) return BadRequest(problem);
        template.Id = 0;
        template.CreatedAt = DateTime.UtcNow;
        _db.OrderTemplates.Add(template);
        await _db.SaveChangesAsync();
        return Ok(template);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] OrderTemplate updated)
    {
        var template = await _db.OrderTemplates.FindAsync(id);
        if (template == null) return NotFound();

        var problem = ValidateAndNormalize(updated);
        if (problem != null) return BadRequest(problem);

        template.Name = updated.Name;
        template.Symbol = updated.Symbol;
        template.Side = updated.Side;
        template.PriceOffsetPct = updated.PriceOffsetPct;
        template.Quantity = updated.Quantity;
        template.QtyPct = updated.QtyPct;
        template.Note = updated.Note;
        await _db.SaveChangesAsync();
        return Ok(template);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var template = await _db.OrderTemplates.FindAsync(id);
        if (template == null) return NotFound();
        _db.OrderTemplates.Remove(template);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
