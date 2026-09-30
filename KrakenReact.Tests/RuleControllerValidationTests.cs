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
