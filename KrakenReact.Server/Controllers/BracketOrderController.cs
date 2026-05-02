using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/bracketorders")]
public class BracketOrderController : ControllerBase
{
    private readonly KrakenDbContext _db;

    public BracketOrderController(KrakenDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.BracketOrders.AsNoTracking().OrderByDescending(b => b.CreatedAt).ToListAsync());

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var b = await _db.BracketOrders.FindAsync(id);
        if (b == null) return NotFound();
        _db.BracketOrders.Remove(b);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var b = await _db.BracketOrders.FindAsync(id);
        if (b == null) return NotFound();
        if (b.Status != "Watching" && b.Status != "Active") return BadRequest("Only Watching or Active brackets can be cancelled");
        b.Status = "Cancelled";
        await _db.SaveChangesAsync();
        return Ok(b);
    }
}
