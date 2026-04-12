using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Kraken.Net.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using KrakenReact.Server.Hubs;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly TradingStateService _state;
    private readonly KrakenRestService _kraken;
    private readonly IHubContext<TradingHub> _hub;

    public OrdersController(TradingStateService state, KrakenRestService kraken, IHubContext<TradingHub> hub)
    {
        _state = state;
        _kraken = kraken;
        _hub = hub;
    }

    [HttpGet]
    public ActionResult<List<OrderDto>> GetAll()
    {
        // Recalculate fields to ensure latest prices/distances are fresh
        _state.RecalculateAllOrderFields();
        return Ok(_state.Orders.Values.OrderByDescending(o => o.CreateTime).ToList());
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] CreateOrderRequest req)
    {
        var side = req.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell;
        var clientOrderId = $"UI{DateTime.Now:yyyyMMddHHmmss}";
        var result = await _kraken.PlaceOrderAsync(req.Symbol.Replace("/", ""), side, OrderType.Limit, req.Quantity, req.Price, clientOrderId);
        if (!result.Success)
            return BadRequest(new { error = result.Error?.Message ?? "Failed to place order" });

        // Add the new order(s) to state immediately with calculated fields
        foreach (var orderId in result.Data.OrderIds)
        {
            var dto = new OrderDto
            {
                Id = orderId,
                Symbol = req.Symbol.Replace("/", ""),
                Side = req.Side,
                Type = "Limit",
                Status = "Open",
                Price = req.Price,
                Quantity = req.Quantity,
                CreateTime = DateTime.UtcNow,
                ClientOrderId = clientOrderId
            };
            _state.RecalculateOrderFields(dto);
            _state.Orders[orderId] = dto;
        }

        // Recalculate balance covered/uncovered amounts
        _state.RecalculateBalanceCoveredAmounts();

        // Broadcast updates to all clients
        _ = Task.Run(async () =>
        {
            try
            {
                await _hub.Clients.All.SendAsync("OrderUpdate", _state.Orders.Values.ToList());
                await _hub.Clients.All.SendAsync("BalanceUpdate", _state.Balances.Values.ToList());
            }
            catch { /* Ignore broadcast errors */ }
        });

        return Ok(new { orderIds = result.Data.OrderIds });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Amend(string id, [FromBody] AmendOrderRequest req)
    {
        // Find the order to get symbol
        var order = _state.Orders.Values.FirstOrDefault(o => o.Id == id);
        if (order == null) return NotFound();

        var orderResult = await _kraken.AmendOrderValues(id, order.Symbol, req.Price, req.Quantity);
        if (!orderResult.Success)
            return BadRequest(new { error = $"Failed to amend order {orderResult.Error?.ErrorDescription}" });

        // Update the order with new values
        order.Price = req.Price;
        order.Quantity = req.Quantity;

        // Recalculate all derived fields
        _state.RecalculateOrderFields(order);

        // Recalculate balance covered/uncovered amounts
        _state.RecalculateBalanceCoveredAmounts();

        // Broadcast updates to all clients
        _ = Task.Run(async () =>
        {
            try
            {
                await _hub.Clients.All.SendAsync("OrderUpdate", _state.Orders.Values.ToList());
                await _hub.Clients.All.SendAsync("BalanceUpdate", _state.Balances.Values.ToList());
            }
            catch { /* Ignore broadcast errors */ }
        });

        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Cancel(string id)
    {
        var success = await _kraken.CancelOrderAsync(id);
        if (!success)
            return BadRequest(new { error = "Failed to cancel order" });

        // Recalculate balance covered/uncovered amounts after order cancellation
        _state.RecalculateBalanceCoveredAmounts();

        // Broadcast balance updates to all clients
        _ = Task.Run(async () =>
        {
            try
            {
                await _hub.Clients.All.SendAsync("BalanceUpdate", _state.Balances.Values.ToList());
            }
            catch { /* Ignore broadcast errors */ }
        });

        return Ok();
    }
}
