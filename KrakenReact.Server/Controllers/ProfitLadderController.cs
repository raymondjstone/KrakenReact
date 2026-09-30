using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
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

    /// <summary>The rules for a ladder rung, applied to both Create and Update. (Update used to check nothing, so a PUT could
    /// set a negative trigger — the ladder then reads every price as past its trigger and sells its share at once.)</summary>
    internal static string? Validate(ProfitLadderRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Symbol)) return "Symbol is required";
        if (rule.TriggerPct <= 0 || rule.TriggerPct > 1000) return "TriggerPct must be greater than 0 and at most 1000";
        if (rule.SellPct is <= 0 or > 100) return "SellPct must be greater than 0 and at most 100";
        if (rule.CooldownHours > 24 * 365) return "CooldownHours must be at most 8760"; // a negative value is clamped to 0, not rejected
        return null;
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] ProfitLadderRule rule)
    {
        var problem = Validate(rule);
        if (problem != null) return BadRequest(new { message = problem });

        rule.Symbol = rule.Symbol.Trim();
        rule.CooldownHours = Math.Max(0, rule.CooldownHours);
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

        var problem = Validate(rule);
        if (problem != null) return BadRequest(new { message = problem });

        existing.Symbol = rule.Symbol.Trim();
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
