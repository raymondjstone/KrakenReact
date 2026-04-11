using System.Text.Json;
using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class WebSocketV2MessageTests
{
    [Fact]
    public void ExecutionWsData_DeserializesSnakeCaseProperties()
    {
        var json = """
        {
            "order_id": "ABC123",
            "symbol": "XBT/USD",
            "side": "Buy",
            "order_type": "Limit",
            "limit_price": 50000.5,
            "order_qty": 0.1,
            "order_status": "Open",
            "timestamp": "2026-01-15T10:30:00Z"
        }
        """;

        var data = JsonSerializer.Deserialize<ExecutionWsData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(data);
        Assert.Equal("ABC123", data!.OrderId);
        Assert.Equal("XBT/USD", data.Symbol);
        Assert.Equal("Buy", data.Side);
        Assert.Equal("Limit", data.OrderType);
        Assert.Equal(50000.5m, data.LimitPrice);
        Assert.Equal(0.1m, data.OrderQty);
        Assert.Equal("Open", data.OrderStatus);
    }

    [Fact]
    public void ExecutionWsMessage_DeserializesFullMessage()
    {
        var json = """
        {
            "channel": "executions",
            "type": "update",
            "data": [
                {
                    "order_id": "O1",
                    "symbol": "SOL/USD",
                    "side": "Sell",
                    "order_type": "Limit",
                    "limit_price": 150.0,
                    "order_qty": 5.0,
                    "order_status": "Open",
                    "timestamp": "2026-01-15T10:30:00Z"
                }
            ]
        }
        """;

        var msg = JsonSerializer.Deserialize<ExecutionWsMessage>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(msg);
        Assert.Equal("executions", msg!.Channel);
        Assert.Equal("update", msg.Type);
        Assert.Single(msg.Data!);
        Assert.Equal("O1", msg.Data![0].OrderId);
    }

    [Fact]
    public void BalanceWsMessage_Deserializes()
    {
        var json = """
        {
            "channel": "balances",
            "type": "snapshot",
            "data": [
                { "asset": "XBT", "balance": 1.5 },
                { "asset": "USD", "balance": 10000.0 }
            ]
        }
        """;

        var msg = JsonSerializer.Deserialize<BalanceWsMessage>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(msg);
        Assert.Equal("balances", msg!.Channel);
        Assert.Equal(2, msg.Data!.Count);
        Assert.Equal("XBT", msg.Data[0].Asset);
        Assert.Equal(1.5, msg.Data[0].Balance);
        Assert.Equal("USD", msg.Data[1].Asset);
        Assert.Equal(10000.0, msg.Data[1].Balance);
    }

    [Fact]
    public void ExecutionWsData_DefaultValues_WhenFieldsMissing()
    {
        // Simulates a partial update that only sends status change
        var json = """
        {
            "order_id": "O1",
            "order_status": "filled"
        }
        """;

        var data = JsonSerializer.Deserialize<ExecutionWsData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(data);
        Assert.Equal("O1", data!.OrderId);
        Assert.Equal("filled", data.OrderStatus);
        Assert.Null(data.Symbol); // Not sent in partial update
        Assert.Equal(0m, data.LimitPrice); // Default decimal
        Assert.Equal(0m, data.OrderQty);
    }
}
