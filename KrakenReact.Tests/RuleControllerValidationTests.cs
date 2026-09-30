using Hangfire;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace KrakenReact.Tests;

/// <summary>Create and Update must apply the same rules; Update used to check nothing on several of these endpoints.</summary>
public class ProfitLadderControllerValidationTests : IDisposable
{
    private readonly KrakenDbContext _db = new(new DbContextOptionsBuilder<KrakenDbContext>().UseInMemoryDatabase($"pl-{Guid.NewGuid()}").Options);
    public void Dispose() => _db.Dispose();

    private static ProfitLadderRule Good() => new() { Symbol = "BTC/USD", TriggerPct = 10m, SellPct = 25m, CooldownHours = 24 };

    [Theory]
    [InlineData(0, 25)]
    [InlineData(-100, 25)]      // a negative trigger reads every price as past it
    [InlineData(5000, 25)]
    [InlineData(10, 0)]
    [InlineData(10, 150)]
    public async Task Create_RejectsBadValues(double trigger, double sell)
    {
        var r = Good(); r.TriggerPct = (decimal)trigger; r.SellPct = (decimal)sell;
        Assert.IsType<BadRequestObjectResult>(await new ProfitLadderController(_db).Create(r));
        Assert.Empty(_db.ProfitLadderRules);
    }

    [Theory]
    [InlineData(-100, 25)]
    [InlineData(0, 25)]
    [InlineData(10, 500)]
    [InlineData(10, -5)]
    public async Task Update_AppliesTheSameRules_AndLeavesTheRuleUntouched(double trigger, double sell)
    {
        var ctrl = new ProfitLadderController(_db);
        var created = (ProfitLadderRule)((OkObjectResult)await ctrl.Create(Good())).Value!;

        var bad = Good(); bad.TriggerPct = (decimal)trigger; bad.SellPct = (decimal)sell;
        Assert.IsType<BadRequestObjectResult>(await ctrl.Update(created.Id, bad));

        var stored = await _db.ProfitLadderRules.AsNoTracking().SingleAsync();
        Assert.Equal(10m, stored.TriggerPct);
        Assert.Equal(25m, stored.SellPct);
    }

    [Fact]
    public async Task ValidUpdate_IsApplied()
    {
        var ctrl = new ProfitLadderController(_db);
        var created = (ProfitLadderRule)((OkObjectResult)await ctrl.Create(Good())).Value!;

        var change = Good(); change.TriggerPct = 20m; change.Symbol = "  ETH/USD ";
        Assert.IsType<OkObjectResult>(await ctrl.Update(created.Id, change));

        var stored = await _db.ProfitLadderRules.AsNoTracking().SingleAsync();
        Assert.Equal(20m, stored.TriggerPct);
        Assert.Equal("ETH/USD", stored.Symbol);   // trimmed
    }
}

public class DcaControllerValidationTests : IDisposable
{
    private readonly KrakenDbContext _db = new(new DbContextOptionsBuilder<KrakenDbContext>().UseInMemoryDatabase($"dca-{Guid.NewGuid()}").Options);
    private readonly Mock<IRecurringJobManager> _jobs = new();
    public void Dispose() => _db.Dispose();

    private DcaController Ctrl() => new(_db, _jobs.Object);
    private static DcaRule Good() => new() { Symbol = "SOL/USD", AmountUsd = 50m, CronExpression = "0 9 * * 1" };

    [Fact]
    public async Task AnInvalidCron_IsRefusedBeforeAnythingIsSaved()
    {
        var r = Good(); r.CronExpression = "every monday";

        var result = await Ctrl().Create(r);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.DcaRules);          // used to be saved first, then rejected by the scheduler, leaving a rule that never ran
    }

    [Theory]
    [InlineData("", 50, "0 9 * * 1")]
    [InlineData("SOL/USD", 0, "0 9 * * 1")]
    [InlineData("SOL/USD", -5, "0 9 * * 1")]
    [InlineData("SOL/USD", 50, "")]
    [InlineData("SOL/USD", 50, "0 9 * *")]
    public async Task Update_AppliesTheSameRulesAsCreate(string symbol, double amount, string cron)
    {
        var ctrl = Ctrl();
        var created = (DcaRule)((OkObjectResult)await ctrl.Create(Good())).Value!;

        var bad = Good(); bad.Symbol = symbol; bad.AmountUsd = (decimal)amount; bad.CronExpression = cron;
        Assert.IsType<BadRequestObjectResult>(await ctrl.Update(created.Id, bad));

        var stored = await _db.DcaRules.AsNoTracking().SingleAsync();
        Assert.Equal("SOL/USD", stored.Symbol);
        Assert.Equal(50m, stored.AmountUsd);
        Assert.Equal("0 9 * * 1", stored.CronExpression);
    }

    [Fact]
    public async Task OptionalFeatures_AreValidatedOnlyWhenSwitchedOn()
    {
        var off = Good(); off.ConditionalMaPeriod = 0; off.AtrRiskUsd = 0; off.FearGreedMaxIndex = 500;   // irrelevant while off
        Assert.IsType<OkObjectResult>(await Ctrl().Create(off));

        var on = Good(); on.ConditionalEnabled = true; on.ConditionalMaPeriod = 0;
        Assert.IsType<BadRequestObjectResult>(await Ctrl().Create(on));

        var atr = Good(); atr.AtrSizingEnabled = true; atr.AtrRiskUsd = 0;
        Assert.IsType<BadRequestObjectResult>(await Ctrl().Create(atr));

        var fg = Good(); fg.FearGreedEnabled = true; fg.FearGreedMaxIndex = 101;
        Assert.IsType<BadRequestObjectResult>(await Ctrl().Create(fg));
    }
}

public class RebalanceControllerValidationTests : IDisposable
{
    private readonly KrakenDbContext _db = new(new DbContextOptionsBuilder<KrakenDbContext>().UseInMemoryDatabase($"reb-{Guid.NewGuid()}").Options);
    private readonly Mock<IRecurringJobManager> _jobs = new();
    public void Dispose() => _db.Dispose();

    private RebalanceScheduleController Ctrl() => new(_db, _jobs.Object);
    private static RebalanceSchedule Good() => new() { Targets = "BTC:50,ETH:30,USD:20", CronExpression = "0 9 * * 1", DriftMinPct = 5m };

    [Theory]
    [InlineData("BTC:4O,ETH:50", "0 9 * * 1", 5)]          // letter O: used to be silently dropped
    [InlineData("BTC:70,ETH:70", "0 9 * * 1", 5)]          // 140%
    [InlineData("BTC:50,BTC:20", "0 9 * * 1", 5)]          // duplicate asset
    [InlineData("", "0 9 * * 1", 5)]
    [InlineData("BTC:50,ETH:50", "nonsense", 5)]           // bad cron used to be saved, then break the restore at startup
    [InlineData("BTC:50,ETH:50", "0 9 * * 1", 0)]          // a drift of 0 would trade on every run
    [InlineData("BTC:50,ETH:50", "0 9 * * 1", 500)]
    public async Task Create_RejectsAndSavesNothing(string targets, string cron, double drift)
    {
        var s = Good(); s.Targets = targets; s.CronExpression = cron; s.DriftMinPct = (decimal)drift;

        Assert.IsType<BadRequestObjectResult>(await Ctrl().Create(s));
        Assert.Empty(_db.RebalanceSchedules);
        _jobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_AppliesTheSameRules_AndKeepsTheOldValues()
    {
        var ctrl = Ctrl();
        var created = (RebalanceSchedule)((OkObjectResult)await ctrl.Create(Good())).Value!;

        var bad = Good(); bad.Targets = "BTC:90,ETH:90";
        Assert.IsType<BadRequestObjectResult>(await ctrl.Update(created.Id, bad));

        Assert.Equal("BTC:50,ETH:30,USD:20", (await _db.RebalanceSchedules.AsNoTracking().SingleAsync()).Targets);
    }

    [Fact]
    public async Task Update_OfAMissingSchedule_IsStill404()
    {
        var bad = Good(); bad.Targets = "junk";
        Assert.IsType<NotFoundResult>(await Ctrl().Update(999, bad));
    }
}

public class AutoRepriceControllerValidationTests : IDisposable
{
    private readonly KrakenDbContext _db = new(new DbContextOptionsBuilder<KrakenDbContext>().UseInMemoryDatabase($"rep-{Guid.NewGuid()}").Options);
    public void Dispose() => _db.Dispose();

    private AutoRepriceController Ctrl() => new(_db, new Mock<IBackgroundJobClient>().Object);
    private static AutoRepriceRule Good() => new() { Symbol = "XBT/USD", MaxDeviationPct = 2m, MinAgeMinutes = 15, MaxAgeMinutes = 0, NewPriceOffsetPct = 0.1m };

    [Theory]
    [InlineData(0, 15, 0, 0)]        // no deviation threshold
    [InlineData(150, 15, 0, 0)]
    [InlineData(2, 0, 0, 0)]         // min age
    [InlineData(2, 15, -1, 0)]
    [InlineData(2, 15, 10, 0)]       // max age not above min age
    [InlineData(2, 15, 0, 100)]      // an offset of 100% prices a buy at zero
    [InlineData(2, 15, 0, -5)]
    public async Task Create_And_Update_RejectTheSameBadValues(double dev, int min, int max, double offset)
    {
        var ctrl = Ctrl();
        var bad = Good(); bad.MaxDeviationPct = (decimal)dev; bad.MinAgeMinutes = min; bad.MaxAgeMinutes = max; bad.NewPriceOffsetPct = (decimal)offset;
        Assert.IsType<BadRequestObjectResult>(await ctrl.Create(bad));

        var created = (AutoRepriceRule)((OkObjectResult)await ctrl.Create(Good())).Value!;
        Assert.IsType<BadRequestObjectResult>(await ctrl.Update(created.Id, bad));   // Update used to check none of it
        Assert.Equal(2m, (await _db.AutoRepriceRules.AsNoTracking().SingleAsync()).MaxDeviationPct);
    }

    [Fact]
    public async Task AMaxAgeAboveTheMinAge_IsFine()
    {
        var r = Good(); r.MaxAgeMinutes = 120;
        Assert.IsType<OkObjectResult>(await Ctrl().Create(r));
    }
}

public class ScheduledOrderControllerValidationTests : IDisposable
{
    private readonly KrakenDbContext _db = new(new DbContextOptionsBuilder<KrakenDbContext>().UseInMemoryDatabase($"sch-{Guid.NewGuid()}").Options);
    public void Dispose() => _db.Dispose();

    private ScheduledOrderController Ctrl() => new(_db);
    private static ScheduledOrder Good() => new() { Symbol = "XBT/USD", Side = "Buy", Price = 50_000m, Quantity = 0.1m, ScheduledAt = DateTime.UtcNow.AddHours(2) };

    [Theory]
    [InlineData("Sell ", "Sell")]      // a trailing space used to slip through and be treated as a BUY
    [InlineData("sell", "Sell")]
    [InlineData(" BUY", "Buy")]
    public async Task TheSideIsNormalized(string sent, string stored)
    {
        var o = Good(); o.Side = sent;
        var result = (ScheduledOrder)((OkObjectResult)await Ctrl().Create(o)).Value!;
        Assert.Equal(stored, result.Side);
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("")]
    [InlineData("Selll")]
    public async Task AnUnknownSide_IsRefused_NotDefaultedToBuy(string side)
    {
        var o = Good(); o.Side = side;
        Assert.IsType<BadRequestObjectResult>(await Ctrl().Create(o));
        Assert.Empty(_db.ScheduledOrders);
    }

    [Fact]
    public async Task Update_AppliesTheSameRules_AndLeavesTheOrderAlone()
    {
        var ctrl = Ctrl();
        var created = (ScheduledOrder)((OkObjectResult)await ctrl.Create(Good())).Value!;

        foreach (var bad in new[]
        {
            new ScheduledOrder { Symbol = "XBT/USD", Side = "Banana", Price = 1m, Quantity = 1m, ScheduledAt = DateTime.UtcNow },
            new ScheduledOrder { Symbol = "XBT/USD", Side = "Buy", Price = 0m, Quantity = 1m, ScheduledAt = DateTime.UtcNow },
            new ScheduledOrder { Symbol = "XBT/USD", Side = "Buy", Price = 1m, Quantity = -3m, ScheduledAt = DateTime.UtcNow },
            new ScheduledOrder { Symbol = "  ", Side = "Buy", Price = 1m, Quantity = 1m, ScheduledAt = DateTime.UtcNow },
        })
            Assert.IsType<BadRequestObjectResult>(await ctrl.Update(created.Id, bad));

        var stored = await _db.ScheduledOrders.AsNoTracking().SingleAsync();
        Assert.Equal(50_000m, stored.Price);
        Assert.Equal(0.1m, stored.Quantity);
        Assert.Equal("Buy", stored.Side);
    }

    [Fact]
    public async Task AZonelessTime_IsTakenAsUtc_SoItIsNotShiftedByTheServersZone()
    {
        var o = Good(); o.ScheduledAt = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Unspecified);
        var result = (ScheduledOrder)((OkObjectResult)await Ctrl().Create(o)).Value!;
        Assert.Equal(DateTimeKind.Utc, result.ScheduledAt.Kind);
        Assert.Equal(9, result.ScheduledAt.Hour);
    }

    [Fact]
    public async Task Twap_RefusesAnUnknownSide()
    {
        var req = new TwapRequest { Symbol = "XBT/USD", Side = "Hold", Price = 1m, TotalQuantity = 1m, Slices = 4, StartAt = DateTime.UtcNow.AddHours(1), EndAt = DateTime.UtcNow.AddHours(5) };
        Assert.IsType<BadRequestObjectResult>(await Ctrl().CreateTwap(req));
        Assert.Empty(_db.ScheduledOrders);
    }

    [Fact]
    public async Task Twap_NormalizesTheSideOntoEverySlice()
    {
        var req = new TwapRequest { Symbol = "XBT/USD", Side = "sell", Price = 1m, TotalQuantity = 1m, Slices = 4, StartAt = DateTime.UtcNow.AddHours(1), EndAt = DateTime.UtcNow.AddHours(5) };
        Assert.IsType<OkObjectResult>(await Ctrl().CreateTwap(req));
        Assert.All(_db.ScheduledOrders, o => Assert.Equal("Sell", o.Side));
        Assert.Equal(4, _db.ScheduledOrders.Count());
    }
}

public class LadderSideValidationTests
{
    private static OrdersController Make()
    {
        var state = new KrakenReact.Server.Services.TradingStateService(new KrakenReact.Server.Services.DelistedPriceService(new Mock<Microsoft.Extensions.Logging.ILogger<KrakenReact.Server.Services.DelistedPriceService>>().Object));
        var db = new DbMethods(new Mock<IDbContextFactory<KrakenDbContext>>().Object, new Mock<Microsoft.Extensions.Logging.ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var kraken = new KrakenReact.Server.Services.KrakenRestService(db, state, new Mock<Microsoft.Extensions.Logging.ILogger<KrakenReact.Server.Services.KrakenRestService>>().Object);
        var hub = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<KrakenReact.Server.Hubs.TradingHub>>();
        return new OrdersController(state, kraken, hub.Object, new Mock<IDbContextFactory<KrakenDbContext>>().Object);
    }

    [Theory]
    [InlineData("Hold")]
    [InlineData("")]
    [InlineData("Selll")]
    public async Task ALadderWithAnUnknownSide_IsRefused_InsteadOfBecomingASell(string side)
    {
        var result = await Make().PlaceLadder(new KrakenReact.Server.DTOs.OrderLadderRequest
        {
            Symbol = "XBTUSD", Side = side, TotalQty = 1m, StartPrice = 100m, EndPrice = 90m, Count = 5,
        });

        Assert.IsType<BadRequestObjectResult>(result);   // it used to place up to 20 SELL orders
    }
}
