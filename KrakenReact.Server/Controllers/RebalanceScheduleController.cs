using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/rebalanceschedules")]
public class RebalanceScheduleController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly IRecurringJobManager _jobs;

    public RebalanceScheduleController(KrakenDbContext db, IRecurringJobManager jobs)
    {
        _db = db;
        _jobs = jobs;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.RebalanceSchedules.AsNoTracking().OrderBy(r => r.Id).ToListAsync());

    /// <summary>Rules for a rebalance schedule, shared by Create and Update. Targets used to be checked only for being non-empty
    /// (malformed entries were silently dropped from the plan, totals over 100% produced orders for more than the portfolio) and
    /// the cron was never checked, so a bad one was saved and then aborted the startup restore of every schedule after it.</summary>
    internal static string? Validate(RebalanceSchedule s)
    {
        if (!InputRules.TryParseTargets(s.Targets, out _, out var targetsError)) return targetsError;
        if (!InputRules.IsValidCron(s.CronExpression, out var cronError)) return cronError;
        if (s.DriftMinPct is < 0.1m or > 100m) return "DriftMinPct must be between 0.1 and 100";
        return null;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RebalanceSchedule s)
    {
        var problem = Validate(s);
        if (problem != null) return BadRequest(problem);

        s.Targets = s.Targets.Trim();
        s.Id = 0;
        s.CreatedAt = DateTime.UtcNow;
        s.LastRunAt = null;
        s.LastRunResult = "";
        _db.RebalanceSchedules.Add(s);
        await _db.SaveChangesAsync();
        ScheduleJob(s);
        return Ok(s);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] RebalanceSchedule updated)
    {
        var s = await _db.RebalanceSchedules.FindAsync(id);
        if (s == null) return NotFound();

        var problem = Validate(updated);
        if (problem != null) return BadRequest(problem);

        s.Targets = updated.Targets.Trim();
        s.CronExpression = updated.CronExpression;
        s.Active = updated.Active;
        s.DriftMinPct = updated.DriftMinPct;
        s.AutoExecute = updated.AutoExecute;
        s.Note = updated.Note;
        await _db.SaveChangesAsync();
        ScheduleJob(s);
        return Ok(s);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await _db.RebalanceSchedules.FindAsync(id);
        if (s == null) return NotFound();
        _jobs.RemoveIfExists($"rebal-{id}");
        _db.RebalanceSchedules.Remove(s);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/trigger")]
    public IActionResult TriggerNow(int id)
    {
        BackgroundJob.Enqueue<RebalanceJob>(j => j.ExecuteAsync(id, CancellationToken.None));
        return Ok(new { message = $"Rebalance schedule {id} enqueued" });
    }

    private void ScheduleJob(RebalanceSchedule s)
    {
        var jobId = $"rebal-{s.Id}";
        if (s.Active)
            _jobs.AddOrUpdate<RebalanceJob>(jobId, j => j.ExecuteAsync(s.Id, CancellationToken.None), s.CronExpression);
        else
            _jobs.RemoveIfExists(jobId);
    }
}
