using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public partial class TradingStateService
{
    /// <summary>Recalculates all calculated fields for a single order (LatestPrice, Distance, DistancePercentage, OrderValue)</summary>
    public void RecalculateOrderFields(OrderDto order)
    {
        // Only look up a price if one hasn't been set yet.
        // V1 WebSocket ticker is the authoritative live-price source and sets order.LatestPrice directly.
        // Calling LatestPrice() here when a live price already exists can return a stale kline and
        // corrupt the value, causing the dashboard price to oscillate.
        if (order.LatestPrice <= 0)
        {
            var baseAsset = NormalizeOrderSymbolBase(order.Symbol);
            var latestPrice = LatestPrice(baseAsset);
            if (latestPrice?.Close > 0)
                order.LatestPrice = latestPrice.Close;
        }

        if (order.LatestPrice > 0)
        {
            order.Distance = order.Price - order.LatestPrice;
            order.DistancePercentage = Math.Round((order.Price - order.LatestPrice) / (order.LatestPrice / 100), 2);
        }
        else
        {
            order.Distance = 0;
            order.DistancePercentage = 0;
        }

        // Recalculate order value (price * quantity)
        order.OrderValue = order.Price * order.Quantity;
    }

    /// <summary>Recalculates all calculated fields for all orders in state</summary>
    public void RecalculateAllOrderFields()
    {
        foreach (var order in Orders.Values)
            RecalculateOrderFields(order);
    }

    /// <summary>Returns true for any order status that means the order is still active/open.
    /// Handles both REST API PascalCase ("Open", "PendingNew") and WebSocket lowercase ("open", "pending_new").</summary>
    public static bool IsOpenOrderStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return false;
        return status.Equals("Open", StringComparison.OrdinalIgnoreCase)
            || status.Equals("New", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PendingNew", StringComparison.OrdinalIgnoreCase)
            || status.Equals("pending_new", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("partially_filled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recalculates covered/uncovered quantities for all balances based on current open orders.
    /// For crypto assets: uses open sell orders to calculate covered/uncovered quantities.
    /// For currency assets (USD, EUR, etc.): reduces available by the total cost of open buy orders in that currency.</summary>
    public void RecalculateBalanceCoveredAmounts()
    {
        foreach (var balance in Balances.Values)
        {
            balance.OrderCoveredQty = 0;
            balance.OrderUncoveredQty = 0;
            balance.OrderCoveredValue = 0;
            balance.OrderUncoveredValue = 0;
        }

        var openOrders = Orders.Values
            .Where(o => IsOpenOrderStatus(o.Status))
            .ToList();

        // Sell orders reduce crypto asset available amounts
        foreach (var order in openOrders.Where(o => o.Side == "Sell"))
        {
            var normalizedBase = NormalizeOrderSymbolBase(order.Symbol);
            if (string.IsNullOrEmpty(normalizedBase)) continue;

            var balance = Balances.Values.FirstOrDefault(b => b.Asset == normalizedBase);
            if (balance == null) continue;

            var orderQty = order.Quantity - order.QuantityFilled;
            balance.OrderCoveredQty += Math.Min(orderQty, balance.Total);
            balance.OrderUncoveredQty = Math.Max(balance.Total - balance.OrderCoveredQty, 0);
            balance.OrderCoveredValue += Math.Min(orderQty, balance.Total) * order.Price;
            balance.OrderUncoveredValue = balance.OrderUncoveredQty * (balance.LatestPrice > 0 ? balance.LatestPrice : order.Price);
        }

        // Buy orders reduce currency balance available amounts
        foreach (var order in openOrders.Where(o => o.Side == "Buy"))
        {
            var quoteCurrency = NormalizeOrderSymbolQuote(order.Symbol);
            if (string.IsNullOrEmpty(quoteCurrency) || !Currency.Contains(quoteCurrency)) continue;

            var balance = Balances.Values.FirstOrDefault(b => b.Asset == quoteCurrency);
            if (balance == null) continue;

            var remainingQty = order.Quantity - order.QuantityFilled;
            var orderCost = remainingQty * order.Price;
            balance.OrderCoveredQty += orderCost;
            balance.OrderCoveredValue += orderCost;
        }

        // Finalize currency balances: set available = total - covered (buy order costs)
        foreach (var balance in Balances.Values.Where(b => Currency.Contains(b.Asset)))
        {
            balance.Available = Math.Max(balance.Total - balance.OrderCoveredQty, 0);
            balance.OrderUncoveredQty = balance.Available;
            balance.OrderUncoveredValue = balance.Available;
        }
    }
}
