using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/scheduledorders")]
public class ScheduledOrderController : ControllerBase
{
    private readonly KrakenDbContext _db;

    public ScheduledOrderController(KrakenDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.ScheduledOrders.AsNoTracking().OrderByDescending(o => o.ScheduledAt).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ScheduledOrder order)
    {
        if (string.IsNullOrWhiteSpace(order.Symbol)) return BadRequest("Symbol required");
        if (order.Price <= 0) return BadRequest("Price must be positive");
        if (order.Quantity <= 0) return BadRequest("Quantity must be positive");
        order.Id = 0;
        order.Status = "Pending";
        order.ExecutedAt = null;
        order.ErrorMessage = "";
        order.CreatedAt = DateTime.UtcNow;
        _db.ScheduledOrders.Add(order);
        await _db.SaveChangesAsync();
        return Ok(order);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ScheduledOrder updated)
    {
        var order = await _db.ScheduledOrders.FindAsync(id);
        if (order == null) return NotFound();
        if (order.Status != "Pending") return BadRequest("Only Pending orders can be updated");
        order.Symbol = updated.Symbol;
        order.Side = updated.Side;
        order.Price = updated.Price;
        order.Quantity = updated.Quantity;
        order.ScheduledAt = updated.ScheduledAt;
        order.Note = updated.Note;
        await _db.SaveChangesAsync();
        return Ok(order);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var order = await _db.ScheduledOrders.FindAsync(id);
        if (order == null) return NotFound();
        _db.ScheduledOrders.Remove(order);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var order = await _db.ScheduledOrders.FindAsync(id);
        if (order == null) return NotFound();
        if (order.Status != "Pending") return BadRequest("Only Pending orders can be cancelled");
        order.Status = "Cancelled";
        await _db.SaveChangesAsync();
        return Ok(order);
    }
}
