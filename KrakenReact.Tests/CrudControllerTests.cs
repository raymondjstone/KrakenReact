using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace KrakenReact.Tests;

internal static class TestDb
{
    public static KrakenDbContext Create()
    {
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"crud-{Guid.NewGuid()}")
            .Options;
        return new KrakenDbContext(options);
    }
}

// ── BracketOrderController ────────────────────────────────────────────────────

public class BracketOrderControllerTests : IDisposable
{
    private readonly KrakenDbContext _db = TestDb.Create();
    private BracketOrderController NewCtrl() => new(_db);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetAll_Empty_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAll());
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public async Task GetAll_OrderedByCreatedAtDesc()
    {
        _db.BracketOrders.AddRange(
            new BracketOrder { Symbol = "AAA", CreatedAt = DateTime.UtcNow.AddHours(-2) },
            new BracketOrder { Symbol = "BBB", CreatedAt = DateTime.UtcNow.AddHours(-1) },
            new BracketOrder { Symbol = "CCC", CreatedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAll());
        var list = ((IEnumerable<BracketOrder>)ok.Value!).ToList();
        Assert.Equal("CCC", list[0].Symbol);
        Assert.Equal("AAA", list[^1].Symbol);
    }

    [Fact]
    public async Task Delete_Missing_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Delete(99));
    }

    [Fact]
    public async Task Delete_Existing_RemovesRow()
    {
        var b = new BracketOrder { Symbol = "XBT/USD", Status = "Watching" };
        _db.BracketOrders.Add(b);
        await _db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await NewCtrl().Delete(b.Id));
        Assert.Empty(await _db.BracketOrders.ToListAsync());
    }

    [Fact]
    public async Task Cancel_Missing_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Cancel(99));
    }

    [Theory]
    [InlineData("TookProfit")]
    [InlineData("Stopped")]
    [InlineData("Cancelled")]
    public async Task Cancel_FinishedStatus_ReturnsBadRequest(string status)
    {
        var b = new BracketOrder { Symbol = "XBT/USD", Status = status };
        _db.BracketOrders.Add(b);
        await _db.SaveChangesAsync();

        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Cancel(b.Id));
    }

    [Theory]
    [InlineData("Watching")]
    [InlineData("Active")]
    public async Task Cancel_LiveStatus_SetsCancelled(string status)
    {
        var b = new BracketOrder { Symbol = "XBT/USD", Status = status };
        _db.BracketOrders.Add(b);
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Cancel(b.Id));
        var returned = Assert.IsType<BracketOrder>(ok.Value);
        Assert.Equal("Cancelled", returned.Status);

        var refreshed = await _db.BracketOrders.FindAsync(b.Id);
        Assert.Equal("Cancelled", refreshed!.Status);
    }
}

// ── OrderTemplateController ───────────────────────────────────────────────────

public class OrderTemplateControllerTests : IDisposable
{
    private readonly KrakenDbContext _db = TestDb.Create();
    private OrderTemplateController NewCtrl() => new(_db);

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("", "BTC/USD")]
    [InlineData("   ", "BTC/USD")]
    [InlineData("name", "")]
    [InlineData("name", "  ")]
    public async Task Create_MissingNameOrSymbol_ReturnsBadRequest(string name, string symbol)
    {
        var t = new OrderTemplate { Name = name, Symbol = symbol };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(t));
    }

    [Fact]
    public async Task Create_ValidTemplate_AssignsIdAndPersists()
    {
        var t = new OrderTemplate { Name = "T1", Symbol = "BTC/USD", Side = "Buy", Quantity = 1m };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(t));
        var saved = Assert.IsType<OrderTemplate>(ok.Value);
        Assert.NotEqual(0, saved.Id);

        Assert.Single(await _db.OrderTemplates.ToListAsync());
    }

    [Fact]
    public async Task Update_Missing_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Update(99, new OrderTemplate { Name = "X", Symbol = "Y" }));
    }

    [Fact]
    public async Task Update_Existing_PatchesFields()
    {
        var existing = new OrderTemplate { Name = "old", Symbol = "BTC/USD", Quantity = 1m };
        _db.OrderTemplates.Add(existing);
        await _db.SaveChangesAsync();

        var updated = new OrderTemplate { Name = "new", Symbol = "ETH/USD", Side = "Sell", Quantity = 2m, Note = "n" };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Update(existing.Id, updated));
        var saved = Assert.IsType<OrderTemplate>(ok.Value);
        Assert.Equal("new", saved.Name);
        Assert.Equal("ETH/USD", saved.Symbol);
        Assert.Equal("Sell", saved.Side);
        Assert.Equal(2m, saved.Quantity);
    }

    [Fact]
    public async Task Delete_Missing_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Delete(99));
    }

    [Fact]
    public async Task Delete_Existing_RemovesRow()
    {
        var t = new OrderTemplate { Name = "x", Symbol = "BTC/USD" };
        _db.OrderTemplates.Add(t);
        await _db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await NewCtrl().Delete(t.Id));
        Assert.Empty(await _db.OrderTemplates.ToListAsync());
    }
}

// ── ProfitLadderController ────────────────────────────────────────────────────

public class ProfitLadderControllerTests : IDisposable
{
    private readonly KrakenDbContext _db = TestDb.Create();
    private ProfitLadderController NewCtrl() => new(_db);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_EmptySymbol_ReturnsBadRequest()
    {
        var rule = new ProfitLadderRule { Symbol = "", TriggerPct = 5m, SellPct = 50m };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(rule));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_NonPositiveTrigger_ReturnsBadRequest(decimal trigger)
    {
        var rule = new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = trigger, SellPct = 50m };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(rule));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Create_InvalidSellPct_ReturnsBadRequest(decimal sellPct)
    {
        var rule = new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 5m, SellPct = sellPct };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(rule));
    }

    [Fact]
    public async Task Create_Valid_ResetsIdAndAuditFields()
    {
        var rule = new ProfitLadderRule
        {
            Id = 999, Symbol = "BTC/USD", TriggerPct = 5m, SellPct = 50m,
            LastTriggeredAt = DateTime.UtcNow, LastResult = "stale"
        };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(rule));
        var saved = Assert.IsType<ProfitLadderRule>(ok.Value);
        Assert.NotEqual(999, saved.Id);
        Assert.Null(saved.LastTriggeredAt);
        Assert.Equal("", saved.LastResult);
    }

    [Fact]
    public async Task Update_NegativeCooldown_ClampedToZero()
    {
        var rule = new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 5m, SellPct = 50m, CooldownHours = 12 };
        _db.ProfitLadderRules.Add(rule);
        await _db.SaveChangesAsync();

        var patch = new ProfitLadderRule
        {
            Symbol = "BTC/USD", TriggerPct = 5m, SellPct = 50m, Active = true, CooldownHours = -5
        };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Update(rule.Id, patch));
        var saved = Assert.IsType<ProfitLadderRule>(ok.Value);
        Assert.Equal(0, saved.CooldownHours);
    }

    [Fact]
    public async Task Update_Missing_ReturnsNotFound()
    {
        var patch = new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 1m, SellPct = 10m };
        Assert.IsType<NotFoundResult>(await NewCtrl().Update(99, patch));
    }

    [Fact]
    public async Task Delete_Existing_RemovesRow()
    {
        var rule = new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 5m, SellPct = 50m };
        _db.ProfitLadderRules.Add(rule);
        await _db.SaveChangesAsync();

        Assert.IsType<OkResult>(await NewCtrl().Delete(rule.Id));
        Assert.Empty(await _db.ProfitLadderRules.ToListAsync());
    }

    [Fact]
    public async Task Delete_Missing_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Delete(99));
    }
}

// ── PriceAlertsController ────────────────────────────────────────────────────

public class PriceAlertsControllerTests : IDisposable
{
    private readonly KrakenDbContext _db = TestDb.Create();
    private PriceAlertsController NewCtrl() => new(_db);

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("", 100)]
    [InlineData("  ", 100)]
    [InlineData("BTC/USD", 0)]
    [InlineData("BTC/USD", -1)]
    public async Task Create_InvalidInputs_ReturnsBadRequest(string symbol, decimal target)
    {
        var req = new CreatePriceAlertRequest(symbol, target, "above", null);
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(req));
    }

    [Fact]
    public async Task Create_DefaultsDirectionToAbove_WhenUnknown()
    {
        var req = new CreatePriceAlertRequest("BTC/USD", 50000m, "weird", "note");
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(req));
        var saved = Assert.IsType<PriceAlert>(ok.Value);
        Assert.Equal("above", saved.Direction);
    }

    [Fact]
    public async Task Create_DirectionBelow_Preserved()
    {
        var req = new CreatePriceAlertRequest("BTC/USD", 50000m, "below", "n");
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(req));
        var saved = Assert.IsType<PriceAlert>(ok.Value);
        Assert.Equal("below", saved.Direction);
    }

    [Fact]
    public async Task Create_DefaultsAutoOrderSideToBuy_WhenUnknown()
    {
        var req = new CreatePriceAlertRequest("BTC/USD", 100m, "above", null,
            AutoOrderEnabled: true, AutoOrderSide: "Weird");
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(req));
        var saved = Assert.IsType<PriceAlert>(ok.Value);
        Assert.Equal("Buy", saved.AutoOrderSide);
    }

    [Fact]
    public async Task Reset_Existing_ReactivatesAndClearsTriggeredAt()
    {
        var alert = new PriceAlert
        {
            Symbol = "BTC/USD", TargetPrice = 100m, Direction = "above",
            Active = false, TriggeredAt = DateTime.UtcNow
        };
        _db.PriceAlerts.Add(alert);
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Reset(alert.Id));
        var saved = Assert.IsType<PriceAlert>(ok.Value);
        Assert.True(saved.Active);
        Assert.Null(saved.TriggeredAt);
    }

    [Fact]
    public async Task Reset_Missing_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Reset(99));
    }

    [Fact]
    public async Task Update_NotFound_ReturnsNotFound()
    {
        var req = new CreatePriceAlertRequest("BTC/USD", 100m, "above", null);
        Assert.IsType<NotFoundResult>(await NewCtrl().Update(99, req));
    }

    [Fact]
    public async Task Update_ChangesFields()
    {
        var alert = new PriceAlert { Symbol = "BTC/USD", TargetPrice = 100m, Direction = "above" };
        _db.PriceAlerts.Add(alert);
        await _db.SaveChangesAsync();

        var req = new CreatePriceAlertRequest("ETH/USD", 3000m, "below", "new note");
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Update(alert.Id, req));
        var saved = Assert.IsType<PriceAlert>(ok.Value);
        Assert.Equal("ETH/USD", saved.Symbol);
        Assert.Equal(3000m, saved.TargetPrice);
        Assert.Equal("below", saved.Direction);
        Assert.Equal("new note", saved.Note);
    }

    [Fact]
    public async Task Delete_Existing_RemovesRow()
    {
        var alert = new PriceAlert { Symbol = "BTC/USD", TargetPrice = 100m };
        _db.PriceAlerts.Add(alert);
        await _db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await NewCtrl().Delete(alert.Id));
        Assert.Empty(await _db.PriceAlerts.ToListAsync());
    }

    [Fact]
    public async Task GetAll_ReturnsAlertsDescendingByCreatedAt()
    {
        _db.PriceAlerts.AddRange(
            new PriceAlert { Symbol = "AAA", TargetPrice = 1m, CreatedAt = DateTime.UtcNow.AddHours(-2) },
            new PriceAlert { Symbol = "BBB", TargetPrice = 1m, CreatedAt = DateTime.UtcNow.AddHours(-1) },
            new PriceAlert { Symbol = "CCC", TargetPrice = 1m, CreatedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAll());
        var list = ((IEnumerable<PriceAlert>)ok.Value!).ToList();
        Assert.Equal("CCC", list[0].Symbol);
        Assert.Equal("AAA", list[^1].Symbol);
    }
}

// ── AutoRepriceController ────────────────────────────────────────────────────

public class AutoRepriceControllerTests : IDisposable
{
    private readonly KrakenDbContext _db = TestDb.Create();
    private readonly Mock<Hangfire.IBackgroundJobClient> _jobs = new();
    private AutoRepriceController NewCtrl() => new(_db, _jobs.Object);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_EmptySymbol_ReturnsBadRequest()
    {
        var rule = new AutoRepriceRule { Symbol = "", MaxDeviationPct = 1m, MinAgeMinutes = 10 };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(rule));
    }

    [Fact]
    public async Task Create_NonPositiveDeviation_ReturnsBadRequest()
    {
        var rule = new AutoRepriceRule { Symbol = "BTC/USD", MaxDeviationPct = 0m, MinAgeMinutes = 10 };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(rule));
    }

    [Fact]
    public async Task Create_MinAgeTooLow_ReturnsBadRequest()
    {
        var rule = new AutoRepriceRule { Symbol = "BTC/USD", MaxDeviationPct = 1m, MinAgeMinutes = 0 };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(rule));
    }

    [Fact]
    public async Task Create_Valid_NormalizesSymbolAndResetsId()
    {
        var rule = new AutoRepriceRule { Id = 999, Symbol = "btcusd", MaxDeviationPct = 1m, MinAgeMinutes = 10 };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(rule));
        var saved = Assert.IsType<AutoRepriceRule>(ok.Value);
        Assert.NotEqual(999, saved.Id);
        // Heuristic: "BTCUSD" → "BTC/USD"
        Assert.Equal("BTC/USD", saved.Symbol);
    }

    [Fact]
    public async Task Create_AlreadySlashedSymbol_StaysUnchanged()
    {
        var rule = new AutoRepriceRule { Symbol = "btc/usd", MaxDeviationPct = 1m, MinAgeMinutes = 10 };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(rule));
        var saved = Assert.IsType<AutoRepriceRule>(ok.Value);
        Assert.Equal("BTC/USD", saved.Symbol);
    }

    [Fact]
    public async Task Update_NotFound_ReturnsNotFound()
    {
        var patch = new AutoRepriceRule { Symbol = "BTC/USD", MaxDeviationPct = 1m, MinAgeMinutes = 10 };
        Assert.IsType<NotFoundResult>(await NewCtrl().Update(99, patch));
    }

    [Fact]
    public async Task Update_Existing_PatchesFields()
    {
        var rule = new AutoRepriceRule { Symbol = "BTC/USD", MaxDeviationPct = 1m, MinAgeMinutes = 10 };
        _db.AutoRepriceRules.Add(rule);
        await _db.SaveChangesAsync();

        var patch = new AutoRepriceRule
        {
            Symbol = "ETH/USD", MaxDeviationPct = 2.5m, MinAgeMinutes = 20,
            MaxAgeMinutes = 120, RepriceBuys = false, RepriceSells = true,
            NewPriceOffsetPct = 0.5m, Active = false
        };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Update(rule.Id, patch));
        var saved = Assert.IsType<AutoRepriceRule>(ok.Value);
        Assert.Equal("ETH/USD", saved.Symbol);
        Assert.Equal(2.5m, saved.MaxDeviationPct);
        Assert.False(saved.RepriceBuys);
        Assert.True(saved.RepriceSells);
        Assert.False(saved.Active);
    }

    [Fact]
    public async Task Delete_Existing_RemovesRow()
    {
        var rule = new AutoRepriceRule { Symbol = "BTC/USD", MaxDeviationPct = 1m, MinAgeMinutes = 10 };
        _db.AutoRepriceRules.Add(rule);
        await _db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await NewCtrl().Delete(rule.Id));
        Assert.Empty(await _db.AutoRepriceRules.ToListAsync());
    }

    [Fact]
    public async Task Delete_Missing_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Delete(99));
    }

    [Fact]
    public void TriggerNow_EnqueuesJobAndReturnsOk()
    {
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().TriggerNow());
        Assert.NotNull(ok.Value);
        _jobs.Verify(
            j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }
}

// ── AlertsController (GetAlerts / Delete) ────────────────────────────────────

public class AlertsControllerTests : IDisposable
{
    private readonly KrakenDbContext _db = TestDb.Create();

    public void Dispose() => _db.Dispose();

    private AlertsController NewCtrl() => new(_db, null!); // NotificationService unused for these endpoints

    [Fact]
    public async Task GetAlerts_Empty_ReturnsZeroTotal()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAlerts());
        var total = (int)ok.Value!.GetType().GetProperty("total")!.GetValue(ok.Value)!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task GetAlerts_OrderedDescending_AndLimitClamped()
    {
        for (int i = 0; i < 5; i++)
            _db.AlertLogs.Add(new AlertLog { Title = $"a{i}", Text = "x", CreatedAt = DateTime.UtcNow.AddMinutes(-i) });
        await _db.SaveChangesAsync();

        // limit < 1 should clamp to 1
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAlerts(limit: 0));
        var alerts = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("alerts")!.GetValue(ok.Value)!;
        Assert.Single(alerts.Cast<object>());
        var total = (int)ok.Value!.GetType().GetProperty("total")!.GetValue(ok.Value)!;
        Assert.Equal(5, total);
    }

    [Fact]
    public async Task Delete_Missing_ReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Delete(99));
    }

    [Fact]
    public async Task Delete_Existing_RemovesRow()
    {
        var alert = new AlertLog { Title = "x", Text = "y" };
        _db.AlertLogs.Add(alert);
        await _db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await NewCtrl().Delete(alert.Id));
        Assert.Empty(await _db.AlertLogs.ToListAsync());
    }
}
