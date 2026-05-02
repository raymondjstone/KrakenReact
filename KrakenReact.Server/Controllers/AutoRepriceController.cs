using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/autoreprice")]
public class AutoRepriceController : ControllerBase
{
    private readonly KrakenDbContext _db;

    public AutoRepriceController(KrakenDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.AutoRepriceRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AutoRepriceRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Symbol)) return BadRequest("Symbol required");
        if (rule.MaxDeviationPct <= 0) return BadRequest("MaxDeviationPct must be positive");
        if (rule.MinAgeMinutes < 1) return BadRequest("MinAgeMinutes must be at least 1");
        rule.Id = 0;
        rule.CreatedAt = DateTime.UtcNow;
        rule.LastResult = "";
        rule.LastRunAt = null;
        _db.AutoRepriceRules.Add(rule);
        await _db.SaveChangesAsync();
        return Ok(rule);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] AutoRepriceRule updated)
    {
        var rule = await _db.AutoRepriceRules.FindAsync(id);
        if (rule == null) return NotFound();
        rule.Symbol = updated.Symbol;
        rule.MaxDeviationPct = updated.MaxDeviationPct;
        rule.MinAgeMinutes = updated.MinAgeMinutes;
        rule.MaxAgeMinutes = updated.MaxAgeMinutes;
        rule.RepriceBuys = updated.RepriceBuys;
        rule.RepriceSells = updated.RepriceSells;
        rule.NewPriceOffsetPct = updated.NewPriceOffsetPct;
        rule.Active = updated.Active;
        await _db.SaveChangesAsync();
        return Ok(rule);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var rule = await _db.AutoRepriceRules.FindAsync(id);
        if (rule == null) return NotFound();
        _db.AutoRepriceRules.Remove(rule);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
