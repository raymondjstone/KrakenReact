using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

internal sealed class InMemoryDbFactory : IDbContextFactory<KrakenDbContext>
{
    private readonly DbContextOptions<KrakenDbContext> _options;
    public InMemoryDbFactory(string name) =>
        _options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase(name)
            .Options;

    public KrakenDbContext CreateDbContext() => new(_options);
}

// ── TradesController ──────────────────────────────────────────────────────────

public class TradesControllerTests : IDisposable
{
    private readonly InMemoryDbFactory _factory;
    private readonly DbMethods _dbm;
    private readonly TradingStateService _state;

    public TradesControllerTests()
    {
        var dbName = $"trades-{Guid.NewGuid()}";
        _factory = new InMemoryDbFactory(dbName);
        _dbm = new DbMethods(_factory, new Mock<ILogger<DbMethods>>().Object);

        var dlog = new Mock<ILogger<DelistedPriceService>>();
        _state = new TradingStateService(new DelistedPriceService(dlog.Object));
    }
    public void Dispose() { /* nothing to dispose */ }

    private TradesController NewCtrl() => new(_dbm, _state);

    private async Task SeedTrade(string id, string symbol, OrderSide side, decimal price, decimal qty, DateTime ts, decimal quoteQty = 0m, decimal fee = 0m, string orderId = "")
    {
        await using var ctx = _factory.CreateDbContext();
        ctx.Trades.Add(new KrakenUserTrade
        {
            Id = id, OrderId = orderId == "" ? id : orderId, Symbol = symbol,
            Side = side, Type = OrderType.Limit, Price = price, Quantity = qty,
            QuoteQuantity = quoteQty == 0m ? price * qty : quoteQty,
            Fee = fee, Timestamp = ts
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAll_Empty_ReturnsEmpty()
    {
        var result = await NewCtrl().GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty((List<TradeDto>)ok.Value!);
    }

    [Fact]
    public async Task GetAll_BuyTrade_NettTotalIncludesFee()
    {
        await SeedTrade("t1", "XBT/USD", OrderSide.Buy, price: 50000m, qty: 0.1m, ts: DateTime.UtcNow, fee: 10m);

        var result = await NewCtrl().GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = (List<TradeDto>)ok.Value!;
        Assert.Single(list);
        // QuoteQuantity = 50000*0.1 = 5000, fee = 10, Buy → NettTotal = 5000 + 10 = 5010
        Assert.Equal(5010m, list[0].NettTotal);
        Assert.Equal("Buy", list[0].Side);
    }

    [Fact]
    public async Task GetAll_SellTrade_NettTotalSubtractsFee()
    {
        await SeedTrade("t1", "XBT/USD", OrderSide.Sell, price: 60000m, qty: 0.1m, ts: DateTime.UtcNow, fee: 12m);

        var result = await NewCtrl().GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = (List<TradeDto>)ok.Value!;
        Assert.Equal(5988m, list[0].NettTotal);
    }

    // ── GetGrouped ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGrouped_NoSymbol_GroupsByOrderId()
    {
        var now = DateTime.UtcNow;
        await SeedTrade("t1", "XBT/USD", OrderSide.Buy, 100m, 1m, now, orderId: "ORDER1", fee: 1m);
        await SeedTrade("t2", "XBT/USD", OrderSide.Buy, 110m, 2m, now, orderId: "ORDER1", fee: 1m);
        await SeedTrade("t3", "ETH/USD", OrderSide.Buy, 3000m, 0.5m, now, orderId: "ORDER2");

        var result = await NewCtrl().GetGrouped();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var groups = (List<TradeDto>)ok.Value!;
        Assert.Equal(2, groups.Count);

        var order1 = groups.First(g => g.OrderId == "ORDER1");
        Assert.Equal(3m, order1.Quantity); // 1 + 2
        // Volume-weighted average: (100*1 + 110*2) / 3 = 320/3 ≈ 106.667
        Assert.True(order1.Price > 106m && order1.Price < 107m);
    }

    [Fact]
    public async Task GetGrouped_WithSymbolFilter_FiltersByBaseAsset()
    {
        var now = DateTime.UtcNow;
        await SeedTrade("t1", "XBT/USD", OrderSide.Buy, 100m, 1m, now, orderId: "O1");
        await SeedTrade("t2", "ETH/USD", OrderSide.Buy, 3000m, 0.5m, now, orderId: "O2");

        var result = await NewCtrl().GetGrouped("XBT/USD");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var groups = (List<TradeDto>)ok.Value!;
        Assert.Single(groups);
        Assert.Equal("XBT/USD", groups[0].Symbol);
    }

    [Fact]
    public async Task GetGrouped_NullSymbol_ReturnsAllTrades()
    {
        await SeedTrade("t1", "XBT/USD", OrderSide.Buy, 100m, 1m, DateTime.UtcNow, orderId: "O1");
        await SeedTrade("t2", "ETH/USD", OrderSide.Buy, 3000m, 0.5m, DateTime.UtcNow, orderId: "O2");
        var result = await NewCtrl().GetGrouped(null);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, ((List<TradeDto>)ok.Value!).Count);
    }

    // ── GetPnl ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPnl_NoTrades_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetPnl());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public async Task GetPnl_BuyThenSell_ComputesProfit()
    {
        var t1 = DateTime.UtcNow.AddDays(-1);
        var t2 = DateTime.UtcNow;
        // Buy 1 BTC at $50k, then sell 1 BTC at $60k → PnL = 10k
        await SeedTrade("b1", "XBT/USD", OrderSide.Buy, 50000m, 1m, t1);
        await SeedTrade("s1", "XBT/USD", OrderSide.Sell, 60000m, 1m, t2);

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetPnl());
        var rows = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Single(rows); // only sells produce rows

        var pnl = (decimal)rows[0].GetType().GetProperty("pnl")!.GetValue(rows[0])!;
        Assert.Equal(10000m, pnl);

        var basis = (decimal)rows[0].GetType().GetProperty("avgCostBasis")!.GetValue(rows[0])!;
        Assert.Equal(50000m, basis);
    }

    [Fact]
    public async Task GetPnl_MultiBuy_RunningAverageCost()
    {
        var t = DateTime.UtcNow;
        // Buy 1 at 100, buy 1 at 300 → avg = 200; sell 1 at 250 → pnl = 50
        await SeedTrade("b1", "SOL/USD", OrderSide.Buy, 100m, 1m, t.AddHours(-3));
        await SeedTrade("b2", "SOL/USD", OrderSide.Buy, 300m, 1m, t.AddHours(-2));
        await SeedTrade("s1", "SOL/USD", OrderSide.Sell, 250m, 1m, t);

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetPnl());
        var rows = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        var pnl = (decimal)rows[0].GetType().GetProperty("pnl")!.GetValue(rows[0])!;
        Assert.Equal(50m, pnl);
    }

    // ── GetSummary ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_NoTrades_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetSummary());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public async Task GetSummary_AggregatesByAsset()
    {
        var t = DateTime.UtcNow;
        await SeedTrade("b1", "XBT/USD", OrderSide.Buy, 50000m, 1m, t.AddHours(-2), quoteQty: 50000m, fee: 100m);
        await SeedTrade("b2", "XBT/USD", OrderSide.Buy, 60000m, 1m, t.AddHours(-1), quoteQty: 60000m, fee: 100m);
        await SeedTrade("s1", "XBT/USD", OrderSide.Sell, 65000m, 0.5m, t, quoteQty: 32500m, fee: 50m);

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetSummary());
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Single(list);

        var first = list[0];
        Assert.Equal("XBT", (string)first.GetType().GetProperty("asset")!.GetValue(first)!);
        Assert.Equal(3, (int)first.GetType().GetProperty("tradeCount")!.GetValue(first)!);
        // bought 2, sold 0.5 → net 1.5
        Assert.Equal(1.5m, (decimal)first.GetType().GetProperty("netQty")!.GetValue(first)!);
        Assert.Equal(250m, (decimal)first.GetType().GetProperty("totalFees")!.GetValue(first)!);
    }
}

// ── LedgerController.GetAll ──────────────────────────────────────────────────

public class LedgerGetAllTests
{
    [Fact]
    public async Task GetAll_Empty_ReturnsEmpty()
    {
        var factory = new InMemoryDbFactory($"ledger-{Guid.NewGuid()}");
        var dbm = new DbMethods(factory, new Mock<ILogger<DbMethods>>().Object);
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        var ctrl = new LedgerController(dbm, state);

        var result = await ctrl.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty((List<LedgerDto>)ok.Value!);
    }

    [Fact]
    public async Task GetAll_ComputesFeePercentage()
    {
        var factory = new InMemoryDbFactory($"ledger-{Guid.NewGuid()}");
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Ledgers.Add(new KrakenLedgerEntry
            {
                Id = "L1", ReferenceId = "R1", Type = LedgerEntryType.Trade,
                Asset = "BTC", Quantity = 1m, Fee = 10m, BalanceAfter = 990m,
                Timestamp = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }
        var dbm = new DbMethods(factory, new Mock<ILogger<DbMethods>>().Object);
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        var ctrl = new LedgerController(dbm, state);

        var result = await ctrl.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = (List<LedgerDto>)ok.Value!;
        Assert.Single(list);
        // FeePercentage = 10 / (990 + 10) * 100 = 1.0
        Assert.Equal(1.0m, list[0].FeePercentage);
    }

    [Fact]
    public async Task GetAll_ZeroBalanceAfter_FeePercentageIsZero()
    {
        var factory = new InMemoryDbFactory($"ledger-{Guid.NewGuid()}");
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Ledgers.Add(new KrakenLedgerEntry
            {
                Id = "L1", ReferenceId = "R1", Type = LedgerEntryType.Deposit,
                Asset = "USD", Quantity = 100m, Fee = 5m, BalanceAfter = 0m,
                Timestamp = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }
        var dbm = new DbMethods(factory, new Mock<ILogger<DbMethods>>().Object);
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        var ctrl = new LedgerController(dbm, state);

        var result = await ctrl.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = (List<LedgerDto>)ok.Value!;
        Assert.Equal(0m, list[0].FeePercentage);
    }
}
