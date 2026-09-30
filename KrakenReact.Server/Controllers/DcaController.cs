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

    /// <summary>Rules for a DCA rule, applied to Create AND Update (Update used to accept an empty symbol, a zero amount, and any
    /// text as the schedule — which the job scheduler then rejected after the rule was already saved).</summary>
    internal static string? Validate(DcaRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Symbol)) return "Symbol required";
        if (rule.AmountUsd <= 0) return "AmountUsd must be positive";
        if (!InputRules.IsValidCron(rule.CronExpression, out var cronError)) return cronError;
        if (rule.ConditionalEnabled && rule.ConditionalMaPeriod is < 2 or > 400) return "The moving-average period must be between 2 and 400 days";
        if (rule.AtrSizingEnabled && rule.AtrRiskUsd <= 0) return "AtrRiskUsd must be positive when ATR sizing is on";
        if (rule.FearGreedEnabled && rule.FearGreedMaxIndex is < 0 or > 100) return "The Fear & Greed limit must be between 0 and 100";
        return null;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DcaRule rule)
    {
        var problem = Validate(rule);
        if (problem != null) return BadRequest(problem);

        rule.Symbol = rule.Symbol.Trim();
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

        var problem = Validate(updated);
        if (problem != null) return BadRequest(problem);

        rule.Symbol = updated.Symbol.Trim();
        rule.AmountUsd = updated.AmountUsd;
        rule.CronExpression = updated.CronExpression;
        rule.Active = updated.Active;
        rule.ConditionalEnabled = updated.ConditionalEnabled;
        rule.ConditionalMaPeriod = updated.ConditionalMaPeriod;
        rule.AtrSizingEnabled = updated.AtrSizingEnabled;
        rule.AtrRiskUsd = updated.AtrRiskUsd;
        rule.FearGreedEnabled = updated.FearGreedEnabled;
        rule.FearGreedMaxIndex = updated.FearGreedMaxIndex;
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
