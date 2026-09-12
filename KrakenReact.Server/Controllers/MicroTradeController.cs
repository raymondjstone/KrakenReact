using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/microtrade")]
public class MicroTradeController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly KrakenRestService _kraken;
    private readonly KrakenWebSocketV2Service _wsV2;
    private readonly ILogger<MicroTradeController> _logger;

    public MicroTradeController(KrakenDbContext db, KrakenRestService kraken, KrakenWebSocketV2Service wsV2, ILogger<MicroTradeController> logger)
    {
        _db = db;
        _kraken = kraken;
        _wsV2 = wsV2;
        _logger = logger;
    }

    /// <summary>Fire-and-forget resubscribe so a new/edited rule's pair gets a live ticker
    /// immediately instead of waiting for the next websocket reconnect.</summary>
    private void ResubscribeTicker()
    {
        _ = Task.Run(async () =>
        {
            try { await _wsV2.ResubscribeTickerAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[MicroTrade] Ticker resubscribe failed"); }
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.MicroTradeRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] MicroTradeRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Symbol)) return BadRequest(new { message = "Symbol is required" });
        if (rule.DropPct <= 0) return BadRequest(new { message = "Drop % must be positive" });
        if (rule.RisePct <= 0) return BadRequest(new { message = "Rise % must be positive" });
        if (rule.BuyOrderTotal <= 0) return BadRequest(new { message = "Buy order total must be positive" });
        if (rule.MaxOrdersPerWindow < 1) return BadRequest(new { message = "Max orders per window must be at least 1" });
        if (rule.WindowHours < 1) return BadRequest(new { message = "Window hours must be at least 1" });
        if (rule.CooldownHours < 0) return BadRequest(new { message = "Cooldown hours cannot be negative" });
        if (rule.StopLossEnabled && rule.StopLossPct is <= 0 or >= 100) return BadRequest(new { message = "Stop loss % must be between 0 and 100" });

        rule.Id = 0;
        rule.CreatedAt = DateTime.UtcNow;
        rule.LastCheckedAt = null;
        rule.LastResult = "";
        _db.MicroTradeRules.Add(rule);
        await _db.SaveChangesAsync();
        ResubscribeTicker();
        return Ok(rule);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] MicroTradeRule updated)
    {
        if (string.IsNullOrWhiteSpace(updated.Symbol)) return BadRequest(new { message = "Symbol is required" });
        if (updated.DropPct <= 0) return BadRequest(new { message = "Drop % must be positive" });
        if (updated.RisePct <= 0) return BadRequest(new { message = "Rise % must be positive" });
        if (updated.BuyOrderTotal <= 0) return BadRequest(new { message = "Buy order total must be positive" });
        if (updated.MaxOrdersPerWindow < 1) return BadRequest(new { message = "Max orders per window must be at least 1" });
        if (updated.WindowHours < 1) return BadRequest(new { message = "Window hours must be at least 1" });
        if (updated.CooldownHours < 0) return BadRequest(new { message = "Cooldown hours cannot be negative" });
        if (updated.StopLossEnabled && updated.StopLossPct is <= 0 or >= 100) return BadRequest(new { message = "Stop loss % must be between 0 and 100" });

        var rule = await _db.MicroTradeRules.FindAsync(id);
        if (rule == null) return NotFound();
        rule.Symbol = updated.Symbol;
        rule.DropPct = updated.DropPct;
        rule.RisePct = updated.RisePct;
        rule.BuyOrderTotal = updated.BuyOrderTotal;
        rule.MaxOrdersPerWindow = updated.MaxOrdersPerWindow;
        rule.WindowHours = updated.WindowHours;
        rule.CooldownHours = updated.CooldownHours;
        rule.StopLossEnabled = updated.StopLossEnabled;
        rule.StopLossPct = updated.StopLossPct;
        rule.Active = updated.Active;
        rule.DryRun = updated.DryRun;
        await _db.SaveChangesAsync();
        ResubscribeTicker();
        return Ok(rule);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var rule = await _db.MicroTradeRules.FindAsync(id);
        if (rule == null) return NotFound();
        _db.MicroTradeRules.Remove(rule);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/trigger")]
    public IActionResult TriggerNow(int id)
    {
        BackgroundJob.Enqueue<MicroTradeJob>(j => j.ExecuteRuleAsync(id, CancellationToken.None));
        return Ok(new { message = $"Micro trade rule {id} check enqueued" });
    }

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders([FromQuery] int? ruleId)
    {
        var query = _db.MicroTradeOrders.AsNoTracking().AsQueryable();
        if (ruleId.HasValue) query = query.Where(o => o.RuleId == ruleId.Value);
        var orders = await query.OrderByDescending(o => o.CreatedAt).Take(200).ToListAsync();
        return Ok(orders);
    }

    [HttpPost("orders/{id:int}/cancel")]
    public async Task<IActionResult> CancelOrder(int id)
    {
        var order = await _db.MicroTradeOrders.FindAsync(id);
        if (order == null) return NotFound();
        if (order.Status != "Buying" && order.Status != "Selling")
            return BadRequest(new { message = "Only Buying or Selling orders can be cancelled" });

        var openOrderId = order.Status == "Buying" ? order.BuyOrderId : order.SellOrderId;
        if (!string.IsNullOrEmpty(openOrderId))
            await _kraken.CancelOrderAsync(openOrderId);

        order.Status = "Cancelled";
        await _db.SaveChangesAsync();
        return Ok(order);
    }
}
