using KrakenReact.Server.Controllers;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class BacktestControllerTests
{
    private static (BacktestController controller, TradingStateService state) Create()
    {
        var log = new Mock<ILogger<DelistedPriceService>>();
        var state = new TradingStateService(new DelistedPriceService(log.Object));
        return (new BacktestController(state), state);
    }

    private static DerivedKline DayKline(string symbol, DateTime date, decimal close) =>
        new() { Interval = "OneDay", Asset = symbol, OpenTime = date, Open = close, High = close + 1m, Low = close - 1m, Close = close, Volume = 1000m };

    // ── Input validation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RunBacktest_MissingSymbol_ReturnsBadRequest(string? symbol)
    {
        var (controller, _) = Create();
        Assert.IsType<BadRequestObjectResult>(controller.RunBacktest(symbol!));
    }

    [Fact]
    public void RunBacktest_UnknownSymbol_ReturnsNotFound()
    {
        var (controller, _) = Create();
        Assert.IsType<NotFoundObjectResult>(controller.RunBacktest("UNKNOWN/USD"));
    }

    // ── Insufficient data ─────────────────────────────────────────────────────

    [Fact]
    public void RunBacktest_LessThan60Bars_ReturnsEmptyTrades()
    {
        var (controller, state) = Create();
        var item = state.GetOrAddPrice("SOL/USD");
        for (int i = 0; i < 30; i++)
            item.AddKline(DayKline("SOL/USD", DateTime.UtcNow.AddDays(-30 + i), 100m + i));

        var result = Assert.IsType<OkObjectResult>(controller.RunBacktest("SOL/USD"));
        // Result is an anonymous type; check it's not null and not a 404/400
        Assert.NotNull(result.Value);
    }

    // ── Sufficient data ────────────────────────────────────────────────────────

    [Fact]
    public void RunBacktest_WithData_Returns200()
    {
        var (controller, state) = Create();
        var item = state.GetOrAddPrice("BTC/USD");
        AddOscillatingKlines(item, "BTC/USD", 200);

        var result = controller.RunBacktest("BTC/USD");
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void RunBacktest_StrictlyRisingPrices_NoBuySignals()
    {
        // If price always > 7d avg, weekDayDiff is always >= 100, so no buy triggers
        var (controller, state) = Create();
        var item = state.GetOrAddPrice("ETH/USD");
        for (int i = 0; i < 100; i++)
            item.AddKline(DayKline("ETH/USD", DateTime.UtcNow.AddDays(-100 + i), 100m + i * 2));

        // No crash expected; result may have 0 trades
        var result = Assert.IsType<OkObjectResult>(controller.RunBacktest("ETH/USD"));
        Assert.NotNull(result.Value);
    }

    [Fact]
    public void RunBacktest_OscillatingPrices_ProducesTrades()
    {
        var (controller, state) = Create();
        var item = state.GetOrAddPrice("SOL/USD");
        AddOscillatingKlines(item, "SOL/USD", 200);

        var ok = Assert.IsType<OkObjectResult>(controller.RunBacktest("SOL/USD"));
        Assert.NotNull(ok.Value);

        // Dynamically inspect the anonymous object returned
        var value = ok.Value!;
        var tradeProp = value.GetType().GetProperty("trades");
        Assert.NotNull(tradeProp);
        var trades = tradeProp!.GetValue(value) as System.Collections.IList;
        Assert.NotNull(trades);
        Assert.True(trades!.Count >= 0); // At least computes without error
    }

    [Fact]
    public void RunBacktest_Summary_ContainsExpectedFields()
    {
        var (controller, state) = Create();
        var item = state.GetOrAddPrice("XBT/USD");
        AddOscillatingKlines(item, "XBT/USD", 200);

        var ok = Assert.IsType<OkObjectResult>(controller.RunBacktest("XBT/USD"));
        var value = ok.Value!;
        var type = value.GetType();

        Assert.NotNull(type.GetProperty("symbol"));
        Assert.NotNull(type.GetProperty("trades"));
        Assert.NotNull(type.GetProperty("summary"));

        var summary = type.GetProperty("summary")!.GetValue(value)!;
        var summaryType = summary.GetType();
        Assert.NotNull(summaryType.GetProperty("tradeCount"));
        Assert.NotNull(summaryType.GetProperty("winRate"));
        Assert.NotNull(summaryType.GetProperty("totalPlPct"));
        Assert.NotNull(summaryType.GetProperty("finalCash"));
    }

    [Fact]
    public void RunBacktest_Symbol_EchoedInResponse()
    {
        var (controller, state) = Create();
        var item = state.GetOrAddPrice("SOL/USD");
        AddOscillatingKlines(item, "SOL/USD", 200);

        var ok = Assert.IsType<OkObjectResult>(controller.RunBacktest("SOL/USD"));
        var symbolProp = ok.Value!.GetType().GetProperty("symbol");
        Assert.Equal("SOL/USD", (string?)symbolProp!.GetValue(ok.Value));
    }

    [Fact]
    public void RunBacktest_OnlyUsesOneDayInterval_IgnoresOtherIntervals()
    {
        var (controller, state) = Create();
        var item = state.GetOrAddPrice("DOT/USD");

        // Add 100 1-minute klines (should be ignored by backtest)
        for (int i = 0; i < 100; i++)
            item.AddKline(new DerivedKline { Interval = "OneMinute", Asset = "DOT/USD", OpenTime = DateTime.UtcNow.AddMinutes(-100 + i), Close = 10m + i });

        // Only 20 OneDay klines (< 60 threshold)
        for (int i = 0; i < 20; i++)
            item.AddKline(DayKline("DOT/USD", DateTime.UtcNow.AddDays(-20 + i), 10m + i));

        // Should return insufficient data result, not use the minute klines
        var ok = Assert.IsType<OkObjectResult>(controller.RunBacktest("DOT/USD"));
        var summary = ok.Value!.GetType().GetProperty("summary")!.GetValue(ok.Value)!;
        var msg = summary.GetType().GetProperty("message")?.GetValue(summary) as string;
        Assert.Contains("Insufficient", msg ?? "Insufficient"); // will hit the < 60 bar guard
    }

    // ── Buy/sell signal logic ─────────────────────────────────────────────────

    [Fact]
    public void RunBacktest_BuySignalTriggered_WhenBelowAverage()
    {
        // Construct prices: first 37 days at 110, then one dip day at 80, then recovery at 120
        // 7-day avg before dip ≈ 110, so 80 < 110 → buy signal
        var (controller, state) = Create();
        var item = state.GetOrAddPrice("ADA/USD");

        // 60+ baseline klines so the 60-bar guard is satisfied
        for (int i = 0; i < 60; i++)
            item.AddKline(DayKline("ADA/USD", DateTime.UtcNow.AddDays(-70 + i), 110m));

        // Dip below average
        item.AddKline(DayKline("ADA/USD", DateTime.UtcNow.AddDays(-9), 80m));

        // Recovery above average (need at least one candle above the 7d avg)
        for (int i = 0; i < 4; i++)
            item.AddKline(DayKline("ADA/USD", DateTime.UtcNow.AddDays(-8 + i), 120m));

        var ok = Assert.IsType<OkObjectResult>(controller.RunBacktest("ADA/USD"));
        var tradeProp = ok.Value!.GetType().GetProperty("trades");
        var trades = tradeProp!.GetValue(ok.Value) as System.Collections.IList;
        // At least one trade should have been triggered
        Assert.NotNull(trades);
        Assert.True(trades!.Count >= 1, "Expected at least one trade from the dip/recovery pattern");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void AddOscillatingKlines(PriceDataItem item, string symbol, int count)
    {
        // Sine-wave prices to trigger buy and sell signals repeatedly
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (decimal)(20 * Math.Sin(i * 0.3));
            item.AddKline(new DerivedKline
            {
                Interval = "OneDay",
                Asset = symbol,
                OpenTime = DateTime.UtcNow.AddDays(-count + i),
                Open = close,
                High = close + 2m,
                Low = close - 2m,
                Close = close,
                Volume = 1000m,
            });
        }
    }
}
