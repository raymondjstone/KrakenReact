using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/alerts")]
public class AlertsController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly NotificationService _notify;

    public AlertsController(KrakenDbContext db, NotificationService notify)
    {
        _db = db;
        _notify = notify;
    }

    [HttpGet]
    public async Task<IActionResult> GetAlerts([FromQuery] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        var alerts = await _db.AlertLogs
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .Select(a => new { a.Id, a.Title, a.Text, a.Type, a.CreatedAt })
            .ToListAsync();
        var unread = await _db.AlertLogs.CountAsync();
        return Ok(new { alerts, total = unread });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var alert = await _db.AlertLogs.FindAsync(id);
        if (alert == null) return NotFound();
        _db.AlertLogs.Remove(alert);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> ClearAll()
    {
        var all = await _db.AlertLogs.ToListAsync();
        _db.AlertLogs.RemoveRange(all);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestAlert()
    {
        await _notify.LogAlert("Test alert", "This is a test notification from the alert centre.", "info");
        return Ok(new { message = "Test alert logged" });
    }
}
