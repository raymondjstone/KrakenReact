using Hangfire;
using Hangfire.Storage;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/schedule")]
public class ScheduleController : ControllerBase
{
    private const string JobId = "daily-price-download";

    private readonly IRecurringJobManager _jobManager;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly JobStorage _jobStorage;

    public ScheduleController(
        IRecurringJobManager jobManager,
        IBackgroundJobClient backgroundJobClient,
        JobStorage jobStorage)
    {
        _jobManager = jobManager;
        _backgroundJobClient = backgroundJobClient;
        _jobStorage = jobStorage;
    }

    /// <summary>GET /api/schedule/price-download — current schedule and last run info</summary>
    [HttpGet("price-download")]
    public IActionResult GetSchedule()
    {
        try
        {
            using var connection = _jobStorage.GetConnection();
            var jobs = connection.GetRecurringJobs();
            var job = jobs.FirstOrDefault(j => j.Id == JobId);
            if (job == null) return NotFound(new { error = "Job not found" });

            return Ok(new
            {
                id = job.Id,
                cron = job.Cron,
                nextExecution = job.NextExecution,
                lastExecution = job.LastExecution,
                lastJobState = job.LastJobState,
                timeZoneId = job.TimeZoneId
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>POST /api/schedule/price-download — update the cron expression</summary>
    /// <remarks>Example body: { "cron": "0 6 * * *" } (run at 6am). Uses standard 5-field cron syntax.</remarks>
    [HttpPost("price-download")]
    public IActionResult UpdateSchedule([FromBody] UpdateScheduleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cron))
            return BadRequest(new { error = "Cron expression is required" });

        try
        {
            _jobManager.AddOrUpdate<DailyPriceRefreshJob>(
                JobId,
                job => job.ExecuteAsync(CancellationToken.None),
                request.Cron,
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

            return Ok(new { message = "Schedule updated", cron = request.Cron });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Invalid cron expression: {ex.Message}" });
        }
    }

    /// <summary>POST /api/schedule/price-download/trigger — run the job immediately</summary>
    [HttpPost("price-download/trigger")]
    public IActionResult TriggerNow()
    {
        try
        {
            _backgroundJobClient.Enqueue<DailyPriceRefreshJob>(job => job.ExecuteAsync(CancellationToken.None));
            return Ok(new { message = "Job enqueued for immediate execution" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

public record UpdateScheduleRequest(string Cron);
