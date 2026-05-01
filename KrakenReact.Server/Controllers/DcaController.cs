using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/dca")]
public class DcaController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly IRecurringJobManager _jobs;

    public DcaController(KrakenDbContext db, IRecurringJobManager jobs)
    {
        _db = db;
        _jobs = jobs;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.DcaRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DcaRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Symbol)) return BadRequest("Symbol required");
        if (rule.AmountUsd <= 0) return BadRequest("AmountUsd must be positive");
        rule.Id = 0;
        rule.CreatedAt = DateTime.UtcNow;
        rule.LastRunAt = null;
        rule.LastRunResult = "";
        _db.DcaRules.Add(rule);
        await _db.SaveChangesAsync();
        ScheduleRule(rule);
        return Ok(rule);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] DcaRule updated)
    {
        var rule = await _db.DcaRules.FindAsync(id);
        if (rule == null) return NotFound();
        rule.Symbol = updated.Symbol;
        rule.AmountUsd = updated.AmountUsd;
        rule.CronExpression = updated.CronExpression;
        rule.Active = updated.Active;
        rule.ConditionalEnabled = updated.ConditionalEnabled;
        rule.ConditionalMaPeriod = updated.ConditionalMaPeriod;
        await _db.SaveChangesAsync();
        ScheduleRule(rule);
        return Ok(rule);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var rule = await _db.DcaRules.FindAsync(id);
        if (rule == null) return NotFound();
        _jobs.RemoveIfExists($"dca-{id}");
        _db.DcaRules.Remove(rule);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/trigger")]
    public IActionResult TriggerNow(int id)
    {
        BackgroundJob.Enqueue<DcaJob>(j => j.ExecuteAsync(id, CancellationToken.None));
        return Ok(new { message = $"DCA rule {id} enqueued" });
    }

    private void ScheduleRule(DcaRule rule)
    {
        var jobId = $"dca-{rule.Id}";
        if (rule.Active)
            _jobs.AddOrUpdate<DcaJob>(jobId, j => j.ExecuteAsync(rule.Id, CancellationToken.None), rule.CronExpression);
        else
            _jobs.RemoveIfExists(jobId);
    }
}
