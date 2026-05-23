using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace KrakenReact.Tests;

// Note: IRecurringJobManager.AddOrUpdate is an extension on the abstract class — these tests
// only verify the controller returns the expected results; the actual scheduling side-effect is
// out-of-scope for unit tests.

// ── DcaController ────────────────────────────────────────────────────────────

public class DcaControllerTests : IDisposable
{
    private readonly KrakenDbContext _db;
    private readonly Mock<IRecurringJobManager> _jobs = new();

    public DcaControllerTests()
    {
        _db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"dca-{Guid.NewGuid()}").Options);
    }
    public void Dispose() => _db.Dispose();

    private DcaController NewCtrl() => new(_db, _jobs.Object);

    [Fact]
    public async Task Create_EmptySymbol_BadRequest()
    {
        var rule = new DcaRule { Symbol = "", AmountUsd = 10m };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(rule));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Create_NonPositiveAmount_BadRequest(decimal amount)
    {
        var rule = new DcaRule { Symbol = "BTC/USD", AmountUsd = amount };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(rule));
    }

    [Fact]
    public async Task Create_Valid_ResetsAuditFields()
    {
        var rule = new DcaRule
        {
            Id = 42, Symbol = "BTC/USD", AmountUsd = 100m,
            LastRunAt = DateTime.UtcNow, LastRunResult = "stale"
        };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(rule));
        var saved = Assert.IsType<DcaRule>(ok.Value);
        Assert.NotEqual(42, saved.Id);
        Assert.Null(saved.LastRunAt);
        Assert.Equal("", saved.LastRunResult);
    }

    [Fact]
    public async Task Update_Missing_NotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Update(99, new DcaRule()));
    }

    [Fact]
    public async Task Update_Existing_PatchesFields()
    {
        var rule = new DcaRule { Symbol = "BTC/USD", AmountUsd = 50m };
        _db.DcaRules.Add(rule);
        await _db.SaveChangesAsync();

        var patch = new DcaRule
        {
            Symbol = "ETH/USD", AmountUsd = 200m, CronExpression = "0 12 * * *",
            Active = false, ConditionalEnabled = true, ConditionalMaPeriod = 50,
            AtrSizingEnabled = true, AtrRiskUsd = 30m,
            FearGreedEnabled = true, FearGreedMaxIndex = 60
        };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Update(rule.Id, patch));
        var saved = Assert.IsType<DcaRule>(ok.Value);
        Assert.Equal("ETH/USD", saved.Symbol);
        Assert.Equal(200m, saved.AmountUsd);
        Assert.Equal("0 12 * * *", saved.CronExpression);
        Assert.False(saved.Active);
        Assert.True(saved.ConditionalEnabled);
        Assert.Equal(50, saved.ConditionalMaPeriod);
        Assert.True(saved.AtrSizingEnabled);
        Assert.Equal(30m, saved.AtrRiskUsd);
        Assert.True(saved.FearGreedEnabled);
        Assert.Equal(60, saved.FearGreedMaxIndex);
    }

    [Fact]
    public async Task Delete_Existing_NoContent()
    {
        var rule = new DcaRule { Symbol = "BTC/USD", AmountUsd = 10m };
        _db.DcaRules.Add(rule);
        await _db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await NewCtrl().Delete(rule.Id));
        _jobs.Verify(j => j.RemoveIfExists($"dca-{rule.Id}"), Times.Once);
    }

    [Fact]
    public async Task Delete_Missing_NotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Delete(99));
    }

    [Fact]
    public async Task GetAll_Empty()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAll());
        Assert.Empty((IEnumerable<DcaRule>)ok.Value!);
    }
}

// ── RebalanceScheduleController ──────────────────────────────────────────────

public class RebalanceScheduleControllerTests : IDisposable
{
    private readonly KrakenDbContext _db;
    private readonly Mock<IRecurringJobManager> _jobs = new();

    public RebalanceScheduleControllerTests()
    {
        _db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"rebal-{Guid.NewGuid()}").Options);
    }
    public void Dispose() => _db.Dispose();

    private RebalanceScheduleController NewCtrl() => new(_db, _jobs.Object);

    [Fact]
    public async Task Create_EmptyTargets_BadRequest()
    {
        var s = new RebalanceSchedule { Targets = "" };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(s));
    }

    [Fact]
    public async Task Create_Valid_PersistsAndResetsAudit()
    {
        var s = new RebalanceSchedule { Id = 42, Targets = "BTC:50,ETH:50", LastRunAt = DateTime.UtcNow, LastRunResult = "stale" };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(s));
        var saved = Assert.IsType<RebalanceSchedule>(ok.Value);
        Assert.NotEqual(42, saved.Id);
        Assert.Null(saved.LastRunAt);
        Assert.Equal("", saved.LastRunResult);
    }

    [Fact]
    public async Task Update_Missing_NotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Update(99, new RebalanceSchedule { Targets = "BTC:100" }));
    }

    [Fact]
    public async Task Update_Existing_AppliesPatch()
    {
        var s = new RebalanceSchedule { Targets = "BTC:50,ETH:50" };
        _db.RebalanceSchedules.Add(s);
        await _db.SaveChangesAsync();

        var patch = new RebalanceSchedule
        {
            Targets = "BTC:60,ETH:40", CronExpression = "0 8 * * *",
            Active = false, DriftMinPct = 3m, AutoExecute = true, Note = "updated"
        };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Update(s.Id, patch));
        var saved = Assert.IsType<RebalanceSchedule>(ok.Value);
        Assert.Equal("BTC:60,ETH:40", saved.Targets);
        Assert.False(saved.Active);
        Assert.True(saved.AutoExecute);
        Assert.Equal(3m, saved.DriftMinPct);
    }

    [Fact]
    public async Task Delete_Existing_NoContent_AndRemovesJob()
    {
        var s = new RebalanceSchedule { Targets = "BTC:100" };
        _db.RebalanceSchedules.Add(s);
        await _db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await NewCtrl().Delete(s.Id));
        _jobs.Verify(j => j.RemoveIfExists($"rebal-{s.Id}"), Times.Once);
    }

    [Fact]
    public async Task Delete_Missing_NotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Delete(99));
    }
}

// ── AutoRepriceController.TriggerNow extra verification ──────────────────────

public class AutoRepriceTriggerTests
{
    [Fact]
    public void TriggerNow_CallsBackgroundJobCreate()
    {
        var db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"trigger-{Guid.NewGuid()}").Options);
        try
        {
            var jobs = new Mock<IBackgroundJobClient>();
            var ctrl = new AutoRepriceController(db, jobs.Object);

            var ok = Assert.IsType<OkObjectResult>(ctrl.TriggerNow());
            Assert.NotNull(ok.Value);
            jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
        }
        finally
        {
            db.Dispose();
        }
    }
}
