using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Kraken.Net.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using KrakenReact.Server.Hubs;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly TradingStateService _state;
    private readonly KrakenRestService _kraken;
    private readonly IHubContext<TradingHub> _hub;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;

    public OrdersController(TradingStateService state, KrakenRestService kraken, IHubContext<TradingHub> hub, IDbContextFactory<KrakenDbContext> dbFactory)
    {
        _state = state;
        _kraken = kraken;
        _hub = hub;
        _dbFactory = dbFactory;
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

        // If bracket params provided, persist bracket for each placed order
        if (req.BracketStopPct.HasValue || req.BracketTakeProfitPct.HasValue)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            foreach (var orderId in result.Data.OrderIds)
            {
                var stopPrice = req.BracketStopPct.HasValue
                    ? Math.Round(req.Price * (1 - req.BracketStopPct.Value / 100m), 2)
                    : 0m;
                var tpPrice = req.BracketTakeProfitPct.HasValue
                    ? Math.Round(req.Price * (1 + req.BracketTakeProfitPct.Value / 100m), 2)
                    : 0m;

                if (stopPrice > 0 || tpPrice > 0)
                {
                    db.BracketOrders.Add(new BracketOrder
                    {
                        KrakenOrderId = orderId,
                        Symbol = req.Symbol,
                        Side = req.Side,
                        Quantity = req.Quantity,
                        EntryPrice = req.Price,
                        StopPrice = stopPrice,
                        TakeProfitPrice = tpPrice,
                        Note = req.BracketNote ?? "",
                        CreatedAt = DateTime.UtcNow,
                    });
                }
            }
            await db.SaveChangesAsync();
        }

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

    /// <summary>POST /api/orders/ladder — place N evenly-spaced limit orders</summary>
    [HttpPost("ladder")]
    public async Task<ActionResult> PlaceLadder([FromBody] OrderLadderRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Symbol)) return BadRequest("Symbol required");
        if (req.Count < 2 || req.Count > 20) return BadRequest("Count must be 2–20");
        if (req.StartPrice <= 0 || req.EndPrice <= 0) return BadRequest("Prices must be positive");
        if (req.TotalQty <= 0) return BadRequest("TotalQty must be positive");

        var side = req.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell;
        var priceStep = (req.EndPrice - req.StartPrice) / (req.Count - 1);
        var qtyEach = Math.Round(req.TotalQty / req.Count, 6);
        var symbol = req.Symbol.Replace("/", "");

        var placed = new List<string>();
        var errors = new List<string>();

        for (int i = 0; i < req.Count; i++)
        {
            var price = Math.Round(req.StartPrice + priceStep * i, 2);
            var clientId = $"ladder{DateTime.Now:yyyyMMddHHmmss}{i}";
            var result = await _kraken.PlaceOrderAsync(symbol, side, OrderType.Limit, qtyEach, price, clientId);
            if (result.Success)
                placed.AddRange(result.Data.OrderIds ?? []);
            else
                errors.Add($"Order {i + 1} @ {price}: {result.Error?.Message}");
        }

        if (errors.Count > 0)
            return Ok(new { placed, errors, message = $"{placed.Count}/{req.Count} orders placed" });

        return Ok(new { placed, message = $"All {req.Count} ladder orders placed" });
    }

    /// <summary>POST /api/orders/close/{asset} — market-sell all available balance of an asset</summary>
    [HttpPost("close/{asset}")]
    public async Task<ActionResult> ClosePosition(string asset)
    {
        asset = Uri.UnescapeDataString(asset);
        if (!_state.Balances.TryGetValue(asset, out var bal))
            return NotFound(new { error = $"No balance found for {asset}" });

        if (bal.Available <= 0)
            return BadRequest(new { error = $"No available quantity for {asset} (locked: {bal.Locked})" });

        // Find the websocket symbol (prefer /USD pair)
        var sym = _state.Symbols.Values.FirstOrDefault(s =>
            TradingStateService.NormalizeAsset(s.BaseAsset) == asset && s.WebsocketName.EndsWith("/USD"))
            ?.WebsocketName.Replace("/", "");

        if (sym == null)
            return BadRequest(new { error = $"Cannot find a USD trading pair for {asset}" });

        var clientId = $"CL{DateTime.Now:yyyyMMddHHmmss}";
        var result = await _kraken.PlaceOrderAsync(sym, Kraken.Net.Enums.OrderSide.Sell, Kraken.Net.Enums.OrderType.Market, bal.Available, 0, clientId);

        if (!result.Success)
            return BadRequest(new { error = result.Error?.Message ?? "Failed to place close order" });

        _ = Task.Run(async () =>
        {
            try { await _hub.Clients.All.SendAsync("OrderUpdate", _state.Orders.Values.ToList()); }
            catch { }
        });

        return Ok(new { message = $"Close order placed for {bal.Available} {asset}", orderIds = result.Data.OrderIds });
    }

    /// <summary>GET /api/orders/debug — diagnostic info for price lookups per order</summary>
    [HttpGet("debug")]
    public ActionResult GetDebug()
    {
        var results = _state.Orders.Values.Select(order =>
        {
            var baseAsset = _state.NormalizeOrderSymbolBase(order.Symbol);
            var latestPrice = _state.LatestPrice(baseAsset);

            // Find matching symbols
            var matchingSymbols = _state.Symbols.Values
                .Where(a =>
                    baseAsset == a.AlternateName ||
                    baseAsset == TradingStateService.NormalizeAsset(a.BaseAsset) ||
                    (a.QuoteAsset == "ZUSD" && (a.BaseAsset == baseAsset || a.AlternateName == baseAsset || a.WebsocketName == baseAsset + "/USD")))
                .Select(a => new { a.WebsocketName, a.BaseAsset, a.AlternateName, a.QuoteAsset,
                    HasPriceEntry = _state.Prices.ContainsKey(a.WebsocketName),
                    BestKline = _state.Prices.TryGetValue(a.WebsocketName, out var pi) ? pi.BestKline?.Close : null
                })
                .ToList();

            // PAXG-specific: show all Prices keys containing PAXG
            var paxgPriceKeys = _state.Prices.Keys.Where(k => k.Contains("PAXG", StringComparison.OrdinalIgnoreCase)).ToList();

            return new
            {
                OrderId = order.Id,
                order.Symbol,
                BaseAsset = baseAsset,
                LatestPriceClose = latestPrice?.Close,
                order.LatestPrice,
                MatchingSymbolsCount = matchingSymbols.Count,
                MatchingSymbols = matchingSymbols,
                PaxgPriceKeys = paxgPriceKeys,
            };
        }).ToList();

        return Ok(results);
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
