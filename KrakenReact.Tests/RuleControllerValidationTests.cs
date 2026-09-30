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
