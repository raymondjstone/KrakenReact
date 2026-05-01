using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
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

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] OrderTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Name)) return BadRequest("Name required");
        if (string.IsNullOrWhiteSpace(template.Symbol)) return BadRequest("Symbol required");
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
