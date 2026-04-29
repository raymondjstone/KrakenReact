using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProfitLadderController : ControllerBase
{
    private readonly KrakenDbContext _db;

    public ProfitLadderController(KrakenDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult> GetAll() =>
        Ok(await _db.ProfitLadderRules.OrderBy(r => r.Symbol).ThenBy(r => r.TriggerPct).ToListAsync());

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] ProfitLadderRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Symbol)) return BadRequest(new { message = "Symbol is required" });
        if (rule.TriggerPct <= 0) return BadRequest(new { message = "TriggerPct must be positive" });
        if (rule.SellPct is <= 0 or > 100) return BadRequest(new { message = "SellPct must be 1–100" });

        rule.Id = 0;
        rule.CreatedAt = DateTime.UtcNow;
        rule.LastTriggeredAt = null;
        rule.LastResult = "";
        _db.ProfitLadderRules.Add(rule);
        await _db.SaveChangesAsync();
        return Ok(rule);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult> Update(int id, [FromBody] ProfitLadderRule rule)
    {
        var existing = await _db.ProfitLadderRules.FindAsync(id);
        if (existing == null) return NotFound();
        existing.Symbol = rule.Symbol;
        existing.TriggerPct = rule.TriggerPct;
        existing.SellPct = rule.SellPct;
        existing.Active = rule.Active;
        existing.CooldownHours = Math.Max(0, rule.CooldownHours);
        await _db.SaveChangesAsync();
        return Ok(existing);
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var rule = await _db.ProfitLadderRules.FindAsync(id);
        if (rule == null) return NotFound();
        _db.ProfitLadderRules.Remove(rule);
        await _db.SaveChangesAsync();
        return Ok();
    }
}
