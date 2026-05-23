using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Tests;

public class ScheduledOrderControllerTests : IDisposable
{
    private readonly KrakenDbContext _db;

    public ScheduledOrderControllerTests()
    {
        _db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"sched-{Guid.NewGuid()}").Options);
    }
    public void Dispose() => _db.Dispose();

    private ScheduledOrderController NewCtrl() => new(_db);

    // ── Create ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", 100, 1)]
    [InlineData("  ", 100, 1)]
    public async Task Create_MissingSymbol_BadRequest(string sym, decimal price, decimal qty)
    {
        var order = new ScheduledOrder { Symbol = sym, Price = price, Quantity = qty, ScheduledAt = DateTime.UtcNow.AddHours(1) };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(order));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_NonPositivePrice_BadRequest(decimal price)
    {
        var order = new ScheduledOrder { Symbol = "BTC/USD", Price = price, Quantity = 1m };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(order));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_NonPositiveQty_BadRequest(decimal qty)
    {
        var order = new ScheduledOrder { Symbol = "BTC/USD", Price = 100m, Quantity = qty };
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Create(order));
    }

    [Fact]
    public async Task Create_Valid_ResetsAuditFieldsAndSetsPending()
    {
        var order = new ScheduledOrder
        {
            Id = 999, Symbol = "BTC/USD", Price = 100m, Quantity = 1m,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            Status = "Executed", ExecutedAt = DateTime.UtcNow, ErrorMessage = "stale"
        };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Create(order));
        var saved = Assert.IsType<ScheduledOrder>(ok.Value);
        Assert.NotEqual(999, saved.Id);
        Assert.Equal("Pending", saved.Status);
        Assert.Null(saved.ExecutedAt);
        Assert.Equal("", saved.ErrorMessage);
    }

    // ── Update ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Missing_NotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Update(99, new ScheduledOrder()));
    }

    [Fact]
    public async Task Update_NonPending_BadRequest()
    {
        var order = new ScheduledOrder { Symbol = "BTC/USD", Status = "Executed", Price = 1m, Quantity = 1m };
        _db.ScheduledOrders.Add(order);
        await _db.SaveChangesAsync();

        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Update(order.Id, new ScheduledOrder { Symbol = "ETH" }));
    }

    [Fact]
    public async Task Update_PendingOrder_AppliesPatch()
    {
        var order = new ScheduledOrder { Symbol = "BTC/USD", Status = "Pending", Price = 1m, Quantity = 1m };
        _db.ScheduledOrders.Add(order);
        await _db.SaveChangesAsync();

        var patch = new ScheduledOrder { Symbol = "ETH/USD", Side = "Sell", Price = 200m, Quantity = 3m, ScheduledAt = DateTime.UtcNow.AddHours(2), Note = "updated" };
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Update(order.Id, patch));
        var saved = Assert.IsType<ScheduledOrder>(ok.Value);
        Assert.Equal("ETH/USD", saved.Symbol);
        Assert.Equal("Sell", saved.Side);
        Assert.Equal(200m, saved.Price);
        Assert.Equal(3m, saved.Quantity);
        Assert.Equal("updated", saved.Note);
    }

    // ── Cancel ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_Missing_NotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Cancel(99));
    }

    [Fact]
    public async Task Cancel_NonPending_BadRequest()
    {
        var order = new ScheduledOrder { Symbol = "BTC/USD", Status = "Executed" };
        _db.ScheduledOrders.Add(order);
        await _db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().Cancel(order.Id));
    }

    [Fact]
    public async Task Cancel_Pending_SetsCancelled()
    {
        var order = new ScheduledOrder { Symbol = "BTC/USD", Status = "Pending" };
        _db.ScheduledOrders.Add(order);
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().Cancel(order.Id));
        var saved = Assert.IsType<ScheduledOrder>(ok.Value);
        Assert.Equal("Cancelled", saved.Status);
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Missing_NotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().Delete(99));
    }

    [Fact]
    public async Task Delete_Existing_NoContent()
    {
        var order = new ScheduledOrder { Symbol = "BTC/USD" };
        _db.ScheduledOrders.Add(order);
        await _db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await NewCtrl().Delete(order.Id));
        Assert.Empty(await _db.ScheduledOrders.ToListAsync());
    }

    // ── GetAll ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_OrderedByScheduledAtDescending()
    {
        _db.ScheduledOrders.AddRange(
            new ScheduledOrder { Symbol = "A", ScheduledAt = DateTime.UtcNow.AddHours(1) },
            new ScheduledOrder { Symbol = "B", ScheduledAt = DateTime.UtcNow.AddHours(3) },
            new ScheduledOrder { Symbol = "C", ScheduledAt = DateTime.UtcNow.AddHours(2) }
        );
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAll());
        var list = ((IEnumerable<ScheduledOrder>)ok.Value!).ToList();
        Assert.Equal(new[] { "B", "C", "A" }, list.Select(o => o.Symbol));
    }

    // ── TWAP ────────────────────────────────────────────────────────────────

    private static TwapRequest MakeTwap(int slices = 4) => new()
    {
        Symbol = "BTC/USD", Side = "Buy", Price = 50000m,
        TotalQuantity = 1m, Slices = slices,
        StartAt = DateTime.UtcNow.AddMinutes(10),
        EndAt = DateTime.UtcNow.AddHours(2),
        Note = "test"
    };

    [Fact]
    public async Task Twap_EmptySymbol_BadRequest()
    {
        var req = MakeTwap();
        req.Symbol = " ";
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().CreateTwap(req));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Twap_NonPositivePrice_BadRequest(decimal price)
    {
        var req = MakeTwap();
        req.Price = price;
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().CreateTwap(req));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Twap_NonPositiveTotalQty_BadRequest(decimal qty)
    {
        var req = MakeTwap();
        req.TotalQuantity = qty;
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().CreateTwap(req));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Twap_OutOfRangeSlices_BadRequest(int slices)
    {
        var req = MakeTwap(slices);
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().CreateTwap(req));
    }

    [Fact]
    public async Task Twap_StartAfterEnd_BadRequest()
    {
        var req = MakeTwap();
        req.StartAt = DateTime.UtcNow.AddHours(3);
        req.EndAt = DateTime.UtcNow.AddHours(1);
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().CreateTwap(req));
    }

    [Fact]
    public async Task Twap_StartInPast_BadRequest()
    {
        var req = MakeTwap();
        req.StartAt = DateTime.UtcNow.AddHours(-1);
        req.EndAt = DateTime.UtcNow.AddHours(1);
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().CreateTwap(req));
    }

    [Fact]
    public async Task Twap_Valid_CreatesAllSlicesPending()
    {
        var req = MakeTwap(slices: 5);
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().CreateTwap(req));
        var countProp = ok.Value!.GetType().GetProperty("count")!.GetValue(ok.Value);
        Assert.Equal(5, countProp);

        var stored = await _db.ScheduledOrders.OrderBy(o => o.ScheduledAt).ToListAsync();
        Assert.Equal(5, stored.Count);
        Assert.All(stored, o => Assert.Equal("Pending", o.Status));
        Assert.All(stored, o => Assert.Equal("BTC/USD", o.Symbol));
        Assert.All(stored, o => Assert.Equal(0.2m, o.Quantity)); // 1.0 / 5
        // Note format
        Assert.StartsWith("TWAP 1/5", stored[0].Note);
        Assert.StartsWith("TWAP 5/5", stored[^1].Note);
    }

    [Fact]
    public async Task Twap_FirstAndLastSliceAtBoundaries()
    {
        // Use a fixed gap divisible by (slices - 1) so integer-tick division is exact.
        var start = DateTime.UtcNow.AddMinutes(10);
        var req = new TwapRequest
        {
            Symbol = "BTC/USD", Side = "Buy", Price = 50000m, TotalQuantity = 1m,
            Slices = 3,
            StartAt = start,
            EndAt = start.AddMinutes(120) // exactly 2h, divisible by (3-1)
        };
        await NewCtrl().CreateTwap(req);
        var stored = await _db.ScheduledOrders.OrderBy(o => o.ScheduledAt).ToListAsync();
        Assert.Equal(req.StartAt, stored[0].ScheduledAt);
        Assert.Equal(req.EndAt, stored[^1].ScheduledAt);
    }
}
