using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/autoreprice")]
public class AutoRepriceController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly IBackgroundJobClient _jobs;

    public AutoRepriceController(KrakenDbContext db, IBackgroundJobClient jobs)
    {
        _db = db;
        _jobs = jobs;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.AutoRepriceRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AutoRepriceRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Symbol)) return BadRequest("Symbol required");
        if (rule.MaxDeviationPct <= 0) return BadRequest("MaxDeviationPct must be positive");
        if (rule.MinAgeMinutes < 1) return BadRequest("MinAgeMinutes must be at least 1");
        rule.Symbol = NormalizeSymbol(rule.Symbol, _db);
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
        rule.Symbol = NormalizeSymbol(updated.Symbol, _db);
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

    private static string NormalizeSymbol(string symbol, KrakenDbContext db)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (symbol.Contains('/')) return symbol;
        // Try DB symbol lookup first (most accurate)
        var ws = db.Symbols
            .Where(s => s.WebsocketName.Replace("/", "") == symbol)
            .Select(s => s.WebsocketName)
            .FirstOrDefault();
        if (ws != null) return ws;
        // Fall back to heuristic: strip known quote currencies from the right
        foreach (var quote in new[] { "USDT", "USDC", "USD", "EUR", "GBP", "BTC", "ETH" })
        {
            if (symbol.EndsWith(quote) && symbol.Length > quote.Length)
                return symbol[..^quote.Length] + "/" + quote;
        }
        return symbol;
    }

    [HttpPost("trigger")]
    public IActionResult TriggerNow()
    {
        _jobs.Enqueue<SmartRepriceJob>(j => j.ExecuteAsync(CancellationToken.None));
        return Ok(new { message = "SmartRepriceJob queued" });
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
