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

    /// <summary>POST /api/scheduledorders/twap — split a large order into N time-weighted slices</summary>
    [HttpPost("twap")]
    public async Task<IActionResult> CreateTwap([FromBody] TwapRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Symbol)) return BadRequest("Symbol required");
        if (req.Price <= 0) return BadRequest("Price must be positive");
        if (req.TotalQuantity <= 0) return BadRequest("TotalQuantity must be positive");
        if (req.Slices < 2 || req.Slices > 100) return BadRequest("Slices must be 2–100");
        if (req.StartAt >= req.EndAt) return BadRequest("StartAt must be before EndAt");
        if (req.StartAt < DateTime.UtcNow.AddMinutes(-1)) return BadRequest("StartAt must be in the future");

        var intervalTicks = (req.EndAt - req.StartAt).Ticks / (req.Slices - 1);
        var sliceQty = Math.Round(req.TotalQuantity / req.Slices, 8);

        var created = new List<ScheduledOrder>();
        for (int i = 0; i < req.Slices; i++)
        {
            var scheduledAt = req.StartAt.AddTicks(intervalTicks * i);
            var order = new ScheduledOrder
            {
                Symbol = req.Symbol.Trim(),
                Side = req.Side,
                Price = req.Price,
                Quantity = sliceQty,
                ScheduledAt = scheduledAt,
                Note = $"TWAP {i + 1}/{req.Slices}" + (string.IsNullOrWhiteSpace(req.Note) ? "" : $" — {req.Note}"),
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
            };
            _db.ScheduledOrders.Add(order);
            created.Add(order);
        }

        await _db.SaveChangesAsync();
        return Ok(new { count = created.Count, sliceQty, message = $"Created {req.Slices} TWAP slices of {sliceQty} {req.Symbol}" });
    }
}

public class TwapRequest
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "Buy";
    public decimal Price { get; set; }
    public decimal TotalQuantity { get; set; }
    public int Slices { get; set; } = 4;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string Note { get; set; } = "";
}
