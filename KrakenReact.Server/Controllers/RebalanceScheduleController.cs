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

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RebalanceSchedule s)
    {
        if (string.IsNullOrWhiteSpace(s.Targets)) return BadRequest("Targets required");
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
        s.Targets = updated.Targets;
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
