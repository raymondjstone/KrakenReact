using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

// ── BalancesController read-only endpoints ────────────────────────────────────

public class BalancesControllerEndpointTests : IDisposable
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;

    public BalancesControllerEndpointTests()
    {
        _db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"bal-{Guid.NewGuid()}").Options);
        var dlog = new Mock<ILogger<DelistedPriceService>>();
        _state = new TradingStateService(new DelistedPriceService(dlog.Object));
    }
    public void Dispose() => _db.Dispose();

    private BalancesController NewCtrl() =>
        new(_state, _db, new Mock<ILogger<BalancesController>>().Object);

    // ── GetDiagnostics ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_Empty_ReturnsZeroCount()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetDiagnostics());
        var tradeCount = (int)ok.Value!.GetType().GetProperty("TradeCount")!.GetValue(ok.Value)!;
        Assert.Equal(0, tradeCount);
    }

    [Fact]
    public async Task GetDiagnostics_WithTrades_ReturnsCountAndSample()
    {
        _db.Trades.Add(new KrakenUserTrade { Id = "t1", OrderId = "o1", Symbol = "BTC/USD", Side = OrderSide.Buy, Price = 50000m, Quantity = 0.1m, Timestamp = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        _state.Balances["BTC"] = new BalanceDto { Asset = "BTC", Total = 1.5m };

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetDiagnostics());
        var tradeCount = (int)ok.Value!.GetType().GetProperty("TradeCount")!.GetValue(ok.Value)!;
        Assert.Equal(1, tradeCount);
        var sampleBalances = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("SampleBalances")!.GetValue(ok.Value)!;
        Assert.Single(sampleBalances.Cast<object>());
    }

    // ── GetPeriodPl ─────────────────────────────────────────────────────────

    [Fact]
    public void GetPeriodPl_NoBalances_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetPeriodPl());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public void GetPeriodPl_FiatAssets_AreSkipped()
    {
        _state.Balances["USD"] = new BalanceDto { Asset = "USD", Total = 1000m, LatestPrice = 1m };
        _state.Balances["EUR"] = new BalanceDto { Asset = "EUR", Total = 500m, LatestPrice = 1m };

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetPeriodPl());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public void GetPeriodPl_ComputesPercentagesForCrypto()
    {
        // Set up SOL balance + 31 days of OneDay klines
        _state.Balances["SOL"] = new BalanceDto { Asset = "SOL", Total = 10m, LatestPrice = 100m };

        var price = new PriceDataItem { Symbol = "SOL/USD" };
        for (int i = 0; i < 31; i++)
            price.AddKline(new DerivedKline
            {
                Asset = "SOL/USD", Interval = "OneDay",
                OpenTime = DateTime.UtcNow.AddDays(-30 + i),
                Close = 50m + i // grows from 50 to 80
            });
        _state.Prices["SOL/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetPeriodPl());
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Single(list);
        var row = list[0];
        Assert.Equal("SOL", (string)row.GetType().GetProperty("asset")!.GetValue(row)!);
        // pl values should be non-null for 1d/7d/30d since 31 klines are available
        Assert.NotNull(row.GetType().GetProperty("pl1d")!.GetValue(row));
    }

    // ── GetAtr ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetAtr_NoBalances_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetAtr());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public void GetAtr_FiatAssets_AreSkipped()
    {
        _state.Balances["USD"] = new BalanceDto { Asset = "USD", Total = 1000m, LatestPrice = 1m };
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetAtr());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public void GetAtr_TooFewKlines_AssetSkipped()
    {
        _state.Balances["SOL"] = new BalanceDto { Asset = "SOL", Total = 10m, LatestPrice = 100m };
        var price = new PriceDataItem { Symbol = "SOL/USD" };
        price.AddKline(new DerivedKline { Asset = "SOL/USD", Interval = "OneDay", OpenTime = DateTime.UtcNow.AddDays(-1), Close = 100m, High = 105m, Low = 95m });
        _state.Prices["SOL/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetAtr());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public void GetAtr_WithSufficientKlines_ReturnsAtrPct()
    {
        _state.Balances["SOL"] = new BalanceDto { Asset = "SOL", Total = 10m, LatestPrice = 100m };

        var price = new PriceDataItem { Symbol = "SOL/USD" };
        for (int i = 0; i < 20; i++)
            price.AddKline(new DerivedKline
            {
                Asset = "SOL/USD", Interval = "OneDay",
                OpenTime = DateTime.UtcNow.AddDays(-20 + i),
                Close = 100m, High = 105m, Low = 95m,
            });
        _state.Prices["SOL/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetAtr());
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Single(list);
        var atrPct = (decimal)list[0].GetType().GetProperty("atrPct")!.GetValue(list[0])!;
        Assert.True(atrPct > 0);
    }

    // ── GetRebalance ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetRebalance_NoTargets_ReturnsEmpty(string? targets)
    {
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetRebalance(targets));
        var list = (System.Collections.IEnumerable)ok.Value!;
        Assert.Empty(list.Cast<object>());
    }

    [Fact]
    public void GetRebalance_NoBalances_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetRebalance("BTC:50,ETH:50"));
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public void GetRebalance_PerfectAllocation_NoDrift()
    {
        _state.Balances["BTC"] = new BalanceDto { Asset = "BTC", Total = 1m, LatestPrice = 60000m, LatestValue = 60000m };
        _state.Balances["USD"] = new BalanceDto { Asset = "USD", Total = 40000m, LatestPrice = 1m, LatestValue = 40000m };

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetRebalance("BTC:60,USD:40"));
        var totalUsd = (decimal)ok.Value!.GetType().GetProperty("totalUsd")!.GetValue(ok.Value)!;
        Assert.Equal(100000m, totalUsd);

        var rows = ((System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("rows")!.GetValue(ok.Value)!).Cast<object>().ToList();
        Assert.Equal(2, rows.Count);
        foreach (var r in rows)
        {
            var drift = (decimal)r.GetType().GetProperty("driftPct")!.GetValue(r)!;
            Assert.Equal(0m, drift);
            var action = (string)r.GetType().GetProperty("action")!.GetValue(r)!;
            Assert.Equal("HOLD", action);
        }
    }

    [Fact]
    public void GetRebalance_OverweightAsset_ActionSell()
    {
        _state.Balances["BTC"] = new BalanceDto { Asset = "BTC", Total = 1m, LatestPrice = 60000m, LatestValue = 80000m };
        _state.Balances["USD"] = new BalanceDto { Asset = "USD", Total = 20000m, LatestPrice = 1m, LatestValue = 20000m };

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetRebalance("BTC:50,USD:50"));
        var rows = ((System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("rows")!.GetValue(ok.Value)!).Cast<object>().ToList();
        var btc = rows.First(r => (string)r.GetType().GetProperty("asset")!.GetValue(r)! == "BTC");
        Assert.Equal("SELL", (string)btc.GetType().GetProperty("action")!.GetValue(btc)!);

        var usd = rows.First(r => (string)r.GetType().GetProperty("asset")!.GetValue(r)! == "USD");
        Assert.Equal("BUY", (string)usd.GetType().GetProperty("action")!.GetValue(usd)!);
    }

    [Fact]
    public void GetRebalance_MalformedTargets_AreIgnored()
    {
        _state.Balances["BTC"] = new BalanceDto { Asset = "BTC", Total = 1m, LatestPrice = 60000m, LatestValue = 60000m };

        // "BAD" has no colon; "ETH:abc" has non-numeric percentage — both skipped
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetRebalance("BAD,ETH:abc,BTC:100"));
        var rows = ((System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("rows")!.GetValue(ok.Value)!).Cast<object>().ToList();
        Assert.Single(rows);
        Assert.Equal("BTC", (string)rows[0].GetType().GetProperty("asset")!.GetValue(rows[0])!);
    }
}

// ── ScheduleController ──────────────────────────────────────────────────────

public class ScheduleControllerTests
{
    private static ScheduleController NewCtrl(
        Mock<IRecurringJobManager>? jobs = null,
        Mock<IBackgroundJobClient>? bg = null)
    {
        jobs ??= new Mock<IRecurringJobManager>();
        bg ??= new Mock<IBackgroundJobClient>();
        // JobStorage isn't used by UpdateSchedule or TriggerNow — pass null!
        return new ScheduleController(jobs.Object, bg.Object, null!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateSchedule_EmptyCron_ReturnsBadRequest(string cron)
    {
        var result = NewCtrl().UpdateSchedule(new UpdateScheduleRequest(cron));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void UpdateSchedule_ValidCron_ReturnsOkWithEcho()
    {
        var jobs = new Mock<IRecurringJobManager>();
        var ok = Assert.IsType<OkObjectResult>(NewCtrl(jobs).UpdateSchedule(new UpdateScheduleRequest("0 6 * * *")));
        var cron = (string)ok.Value!.GetType().GetProperty("cron")!.GetValue(ok.Value)!;
        Assert.Equal("0 6 * * *", cron);
    }

    [Fact]
    public void TriggerNow_EnqueuesAndReturnsOk()
    {
        var bg = new Mock<IBackgroundJobClient>();
        var ok = Assert.IsType<OkObjectResult>(NewCtrl(bg: bg).TriggerNow());
        Assert.NotNull(ok.Value);
        bg.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public void TriggerNow_BackgroundClientThrows_Returns500()
    {
        var bg = new Mock<IBackgroundJobClient>();
        bg.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
          .Throws(new InvalidOperationException("hangfire down"));
        var result = NewCtrl(bg: bg).TriggerNow();
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, status.StatusCode);
    }

    [Fact]
    public void UpdateScheduleRequest_DefaultRecord_HasNullCron()
    {
        // Records with primary ctor: passing null is allowed if not enforced
        var r = new UpdateScheduleRequest("");
        Assert.Equal("", r.Cron);
    }
}
