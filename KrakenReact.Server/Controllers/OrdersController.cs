using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Kraken.Net.Enums;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly TradingStateService _state;
    private readonly KrakenRestService _kraken;

    public OrdersController(TradingStateService state, KrakenRestService kraken)
    {
        _state = state;
        _kraken = kraken;
    }

    [HttpGet]
    public ActionResult<List<OrderDto>> GetAll()
    {
        return Ok(_state.Orders.Values.OrderByDescending(o => o.CreateTime).ToList());
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] CreateOrderRequest req)
    {
        var side = req.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell;
        var clientOrderId = $"UI{DateTime.Now:yyyyMMddHHmmss}";
        var result = await _kraken.PlaceOrderAsync(req.Symbol.Replace("/", ""), side, OrderType.Limit, req.Quantity, req.Price, clientOrderId);
        if (result.Success)
            return Ok(new { orderIds = result.Data.OrderIds });
        return BadRequest(new { error = result.Error?.Message ?? "Failed to place order" });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Amend(string id, [FromBody] AmendOrderRequest req)
    {
        // Find the order to get symbol
        var order = _state.Orders.Values.FirstOrDefault(o => o.Id == id);
        if (order == null) return NotFound();
        var success = await _kraken.AmendOrderValues(id, order.Symbol, req.Price, req.Quantity);
        return success ? Ok() : BadRequest(new { error = "Failed to amend order" });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Cancel(string id)
    {
        var success = await _kraken.CancelOrderAsync(id);
        return success ? Ok() : BadRequest(new { error = "Failed to cancel order" });
    }
}
