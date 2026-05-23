using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Hubs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Kraken.Net.Objects.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

// ── HealthController ─────────────────────────────────────────────────────────

public class HealthControllerTests : IDisposable
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;

    public HealthControllerTests()
    {
        _db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"health-{Guid.NewGuid()}").Options);

        var log = new Mock<ILogger<DelistedPriceService>>();
        _state = new TradingStateService(new DelistedPriceService(log.Object));
    }
    public void Dispose() => _db.Dispose();

    private HealthController NewCtrl() => new(_db, _state);

    private static List<object> ExtractChecks(object resultValue)
    {
        var checks = (System.Collections.IEnumerable)resultValue.GetType().GetProperty("checks")!.GetValue(resultValue)!;
        return checks.Cast<object>().ToList();
    }

    [Fact]
    public async Task Get_EmptyState_ReturnsAllExpectedChecks()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Get());
        var checks = ExtractChecks(ok.Value!);
        // 7 checks total: Database, Symbols, Live Prices, Balances, ML Predictions, Portfolio Snapshot, Initial Load
        Assert.Equal(7, checks.Count);
    }

    [Fact]
    public async Task Get_NoSymbolsAndNoBalances_ReportsNotOk()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Get());
        var allOk = (bool)ok.Value!.GetType().GetProperty("ok")!.GetValue(ok.Value)!;
        Assert.False(allOk);
    }

    [Fact]
    public async Task Get_StalePrice_ReportsLivePricesNotOk()
    {
        // Stale price (>10 min old)
        var price = new PriceDataItem
        {
            Symbol = "BTC/USD",
            KrakenNewPricesLoadedTime = DateTime.UtcNow.AddMinutes(-30)
        };
        _state.Prices["BTC/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Get());
        var checks = ExtractChecks(ok.Value!);
        var livePrices = checks.First(c => (string)c.GetType().GetProperty("name")!.GetValue(c)! == "Live Prices");
        Assert.False((bool)livePrices.GetType().GetProperty("ok")!.GetValue(livePrices)!);
    }

    [Fact]
    public async Task Get_FreshPrice_ReportsLivePricesOk()
    {
        var price = new PriceDataItem
        {
            Symbol = "BTC/USD",
            KrakenNewPricesLoadedTime = DateTime.UtcNow.AddMinutes(-1)
        };
        _state.Prices["BTC/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Get());
        var checks = ExtractChecks(ok.Value!);
        var livePrices = checks.First(c => (string)c.GetType().GetProperty("name")!.GetValue(c)! == "Live Prices");
        Assert.True((bool)livePrices.GetType().GetProperty("ok")!.GetValue(livePrices)!);
    }

    [Fact]
    public async Task Get_InitialLoadFalse_ReportsInitialLoadOk()
    {
        _state.InitialDataLoad = false;
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Get());
        var checks = ExtractChecks(ok.Value!);
        var initial = checks.First(c => (string)c.GetType().GetProperty("name")!.GetValue(c)! == "Initial Load");
        Assert.True((bool)initial.GetType().GetProperty("ok")!.GetValue(initial)!);
    }

    [Fact]
    public async Task Get_FreshPrediction_ReportsMlOk()
    {
        _db.PredictionResults.Add(new PredictionResult
        {
            Symbol = "BTC/USD", Interval = "OneHour", ComputedAt = DateTime.UtcNow.AddHours(-2)
        });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Get());
        var checks = ExtractChecks(ok.Value!);
        var ml = checks.First(c => (string)c.GetType().GetProperty("name")!.GetValue(c)! == "ML Predictions");
        Assert.True((bool)ml.GetType().GetProperty("ok")!.GetValue(ml)!);
    }

    [Fact]
    public async Task Get_StalePrediction_ReportsMlNotOk()
    {
        _db.PredictionResults.Add(new PredictionResult
        {
            Symbol = "BTC/USD", Interval = "OneHour", ComputedAt = DateTime.UtcNow.AddHours(-30)
        });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Get());
        var checks = ExtractChecks(ok.Value!);
        var ml = checks.First(c => (string)c.GetType().GetProperty("name")!.GetValue(c)! == "ML Predictions");
        Assert.False((bool)ml.GetType().GetProperty("ok")!.GetValue(ml)!);
    }

    [Fact]
    public async Task Get_RecentPortfolioSnapshot_ReportsOk()
    {
        _db.PortfolioSnapshots.Add(new PortfolioSnapshot { Date = DateTime.UtcNow.Date, TotalUsd = 100m });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Get());
        var checks = ExtractChecks(ok.Value!);
        var snap = checks.First(c => (string)c.GetType().GetProperty("name")!.GetValue(c)! == "Portfolio Snapshot");
        Assert.True((bool)snap.GetType().GetProperty("ok")!.GetValue(snap)!);
    }
}

// ── ShutdownController ──────────────────────────────────────────────────────

public class ShutdownControllerTests
{
    [Fact]
    public void Shutdown_ReturnsOkAndDoesNotThrow()
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        var hub = new Mock<IHubContext<TradingHub>>();
        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        hub.Setup(h => h.Clients).Returns(clients.Object);
        var log = new Mock<ILogger<ShutdownController>>();

        var ctrl = new ShutdownController(lifetime.Object, hub.Object, log.Object);
        var ok = Assert.IsType<OkObjectResult>(ctrl.Shutdown());
        Assert.NotNull(ok.Value);
    }
}

// ── LedgerController.GetStaking ─────────────────────────────────────────────

public class LedgerStakingTests
{
    private static TradingStateService MakeState()
    {
        var log = new Mock<ILogger<DelistedPriceService>>();
        return new TradingStateService(new DelistedPriceService(log.Object));
    }

    [Fact]
    public void GetStaking_NoCachedLedgers_ReturnsEmpty()
    {
        var state = MakeState();
        var ctrl = new LedgerController(null!, state);
        var ok = Assert.IsType<OkObjectResult>(ctrl.GetStaking());
        var list = (System.Collections.IEnumerable)ok.Value!;
        Assert.Empty(list.Cast<object>());
    }

    [Fact]
    public void GetStaking_OnlyNonStakingLedgers_ReturnsEmpty()
    {
        var state = MakeState();
        state.SetCachedLedgers(new[]
        {
            new KrakenLedgerEntry { Type = Kraken.Net.Enums.LedgerEntryType.Trade, Asset = "BTC", Quantity = 1m, Timestamp = DateTime.UtcNow }
        });
        var ctrl = new LedgerController(null!, state);
        var ok = Assert.IsType<OkObjectResult>(ctrl.GetStaking());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public void GetStaking_FiltersOutSpotFromStakingSubtypes()
    {
        var state = MakeState();
        state.SetCachedLedgers(new[]
        {
            new KrakenLedgerEntry { Type = Kraken.Net.Enums.LedgerEntryType.Staking, SubType = "spotFromStaking", Asset = "BTC", Quantity = 1m, Timestamp = DateTime.UtcNow },
            new KrakenLedgerEntry { Type = Kraken.Net.Enums.LedgerEntryType.Staking, SubType = "spotToStaking",   Asset = "BTC", Quantity = 1m, Timestamp = DateTime.UtcNow }
        });
        var ctrl = new LedgerController(null!, state);
        var ok = Assert.IsType<OkObjectResult>(ctrl.GetStaking());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public void GetStaking_GroupsByNormalizedAsset_AndComputesTotals()
    {
        var state = MakeState();
        var t1 = DateTime.UtcNow.AddDays(-30);
        var t2 = DateTime.UtcNow.AddDays(-1);
        state.SetCachedLedgers(new[]
        {
            new KrakenLedgerEntry { Type = Kraken.Net.Enums.LedgerEntryType.Staking, SubType = "reward", Asset = "XETH", Quantity = 0.5m, Timestamp = t1 },
            new KrakenLedgerEntry { Type = Kraken.Net.Enums.LedgerEntryType.Staking, SubType = "reward", Asset = "ETH",  Quantity = 0.25m, Timestamp = t2 },
        });

        // Provide a price for ETH so totalUsd is non-zero
        var price = new PriceDataItem { Symbol = "ETH/USD" };
        price.AddKline(new DerivedKline { Asset = "ETH/USD", Close = 4000m, OpenTime = DateTime.UtcNow });
        state.Prices["ETH/USD"] = price;

        var ctrl = new LedgerController(null!, state);
        var ok = Assert.IsType<OkObjectResult>(ctrl.GetStaking());
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Single(list);
        var entry = list[0];
        Assert.Equal("ETH", (string)entry.GetType().GetProperty("asset")!.GetValue(entry)!);
        // 0.5 + 0.25 = 0.75
        Assert.Equal(0.75m, (decimal)entry.GetType().GetProperty("totalQty")!.GetValue(entry)!);
        // 0.75 × 4000 = 3000
        Assert.Equal(3000m, (decimal)entry.GetType().GetProperty("totalUsd")!.GetValue(entry)!);
        Assert.Equal(2, (int)entry.GetType().GetProperty("rewardCount")!.GetValue(entry)!);
    }

    [Fact]
    public void GetStaking_OrderedByTotalUsdDescending()
    {
        var state = MakeState();
        state.SetCachedLedgers(new[]
        {
            new KrakenLedgerEntry { Type = Kraken.Net.Enums.LedgerEntryType.Staking, SubType = "reward", Asset = "ETH", Quantity = 0.1m, Timestamp = DateTime.UtcNow },
            new KrakenLedgerEntry { Type = Kraken.Net.Enums.LedgerEntryType.Staking, SubType = "reward", Asset = "DOT", Quantity = 10m,  Timestamp = DateTime.UtcNow },
        });
        var eth = new PriceDataItem { Symbol = "ETH/USD" };
        eth.AddKline(new DerivedKline { Asset = "ETH/USD", Close = 4000m, OpenTime = DateTime.UtcNow });
        var dot = new PriceDataItem { Symbol = "DOT/USD" };
        dot.AddKline(new DerivedKline { Asset = "DOT/USD", Close = 5m, OpenTime = DateTime.UtcNow });
        state.Prices["ETH/USD"] = eth;
        state.Prices["DOT/USD"] = dot;

        var ctrl = new LedgerController(null!, state);
        var ok = Assert.IsType<OkObjectResult>(ctrl.GetStaking());
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        // ETH: 0.1 × 4000 = 400, DOT: 10 × 5 = 50 → ETH first
        Assert.Equal("ETH", (string)list[0].GetType().GetProperty("asset")!.GetValue(list[0])!);
        Assert.Equal("DOT", (string)list[1].GetType().GetProperty("asset")!.GetValue(list[1])!);
    }
}

// ── Model default tests ─────────────────────────────────────────────────────

public class ModelDefaultTests
{
    [Fact]
    public void BracketOrder_Defaults()
    {
        var b = new BracketOrder();
        Assert.Equal("Buy", b.Side);
        Assert.Equal("Watching", b.Status);
        Assert.Equal("", b.KrakenOrderId);
        Assert.Equal("", b.Symbol);
        Assert.Equal("", b.Note);
        Assert.Null(b.ActivatedAt);
        Assert.Null(b.StopOrderId);
    }

    [Fact]
    public void OrderTemplate_Defaults()
    {
        var t = new OrderTemplate();
        Assert.Equal("", t.Name);
        Assert.Equal("Buy", t.Side);
        Assert.Null(t.PriceOffsetPct);
        Assert.Null(t.Quantity);
        Assert.Null(t.QtyPct);
    }

    [Fact]
    public void ProfitLadderRule_Defaults()
    {
        var r = new ProfitLadderRule();
        Assert.Equal("", r.Symbol);
        Assert.True(r.Active);
        Assert.Equal(24, r.CooldownHours);
        Assert.Equal("", r.LastResult);
        Assert.Null(r.LastTriggeredAt);
    }

    [Fact]
    public void AutoRepriceRule_Defaults()
    {
        var r = new AutoRepriceRule();
        Assert.Equal(2.0m, r.MaxDeviationPct);
        Assert.Equal(15, r.MinAgeMinutes);
        Assert.Equal(0, r.MaxAgeMinutes);
        Assert.True(r.RepriceBuys);
        Assert.False(r.RepriceSells);
        Assert.True(r.Active);
    }

    [Fact]
    public void RebalanceSchedule_Defaults()
    {
        var s = new RebalanceSchedule();
        Assert.Equal("", s.Targets);
        Assert.Equal("0 9 * * 1", s.CronExpression);
        Assert.True(s.Active);
        Assert.Equal(5m, s.DriftMinPct);
        Assert.False(s.AutoExecute);
        Assert.Null(s.LastRunAt);
    }

    [Fact]
    public void ScheduledOrder_Defaults()
    {
        var o = new ScheduledOrder();
        Assert.Equal("Buy", o.Side);
        Assert.Equal("Pending", o.Status);
        Assert.Equal("", o.Note);
        Assert.Equal("", o.ErrorMessage);
        Assert.Null(o.ExecutedAt);
    }

    [Fact]
    public void DcaRule_AdvancedDefaults()
    {
        var r = new DcaRule();
        Assert.False(r.ConditionalEnabled);
        Assert.Equal(20, r.ConditionalMaPeriod);
        Assert.False(r.AtrSizingEnabled);
        Assert.Equal(50m, r.AtrRiskUsd);
        Assert.False(r.FearGreedEnabled);
        Assert.Equal(75, r.FearGreedMaxIndex);
    }
}
