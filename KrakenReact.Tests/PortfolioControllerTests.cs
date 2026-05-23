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

public class PortfolioControllerTests : IDisposable
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;
    private readonly PortfolioController _controller;

    public PortfolioControllerTests()
    {
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"portfolio-{Guid.NewGuid()}")
            .Options;
        _db = new KrakenDbContext(options);

        var log = new Mock<ILogger<DelistedPriceService>>();
        _state = new TradingStateService(new DelistedPriceService(log.Object));
        _controller = new PortfolioController(_db, _state);
    }

    public void Dispose() => _db.Dispose();

    // ── GetHistory ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_NoSnapshots_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(await _controller.GetHistory());
        var list = (System.Collections.IEnumerable)ok.Value!;
        Assert.Empty(list.Cast<object>());
    }

    [Fact]
    public async Task GetHistory_ReturnsOrderedByDate()
    {
        _db.PortfolioSnapshots.AddRange(
            new PortfolioSnapshot { Date = DateTime.UtcNow.Date.AddDays(-2), TotalUsd = 200m, TotalGbp = 160m },
            new PortfolioSnapshot { Date = DateTime.UtcNow.Date.AddDays(-1), TotalUsd = 250m, TotalGbp = 200m },
            new PortfolioSnapshot { Date = DateTime.UtcNow.Date,             TotalUsd = 300m, TotalGbp = 240m }
        );
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _controller.GetHistory(7));
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Equal(3, list.Count);

        // Verify ordered ascending by date
        var firstDate = (DateTime)list[0].GetType().GetProperty("date")!.GetValue(list[0])!;
        var lastDate = (DateTime)list[^1].GetType().GetProperty("date")!.GetValue(list[^1])!;
        Assert.True(lastDate > firstDate);
    }

    [Fact]
    public async Task GetHistory_DaysClampedToValidRange()
    {
        // Add an old snapshot beyond 365 days
        _db.PortfolioSnapshots.Add(new PortfolioSnapshot { Date = DateTime.UtcNow.Date.AddDays(-500), TotalUsd = 100m });
        _db.PortfolioSnapshots.Add(new PortfolioSnapshot { Date = DateTime.UtcNow.Date, TotalUsd = 200m });
        await _db.SaveChangesAsync();

        // Days clamped to 365 means we still won't see the -500 snapshot
        var ok = Assert.IsType<OkObjectResult>(await _controller.GetHistory(9999));
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Single(list);
    }

    // ── GetMetrics ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetrics_FewerThanFiveSnapshots_ReturnsNullMetrics()
    {
        for (int i = 0; i < 3; i++)
            _db.PortfolioSnapshots.Add(new PortfolioSnapshot { Date = DateTime.UtcNow.Date.AddDays(-i), TotalUsd = 1000m });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _controller.GetMetrics());
        var sharpe = ok.Value!.GetType().GetProperty("sharpe")!.GetValue(ok.Value);
        Assert.Null(sharpe);
    }

    [Fact]
    public async Task GetMetrics_WithGrowingPortfolio_ReturnsPositiveSharpe()
    {
        for (int i = 0; i < 30; i++)
            _db.PortfolioSnapshots.Add(new PortfolioSnapshot
            {
                Date = DateTime.UtcNow.Date.AddDays(-30 + i),
                TotalUsd = 1000m + i * 10m // strictly growing
            });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _controller.GetMetrics());
        var value = ok.Value!;
        var sampleDays = (int)value.GetType().GetProperty("sampleDays")!.GetValue(value)!;
        var maxDdProp = value.GetType().GetProperty("maxDrawdownPct");
        var annualReturn = value.GetType().GetProperty("annualReturnPct");

        Assert.Equal(30, sampleDays);
        Assert.NotNull(maxDdProp);
        Assert.NotNull(annualReturn);
    }

    [Fact]
    public async Task GetMetrics_ComputesMaxDrawdown_OnDecliningPortfolio()
    {
        // Big peak at start, then steady decline
        var values = new decimal[] { 1000, 1100, 1200, 1100, 900, 800, 700, 600 };
        for (int i = 0; i < values.Length; i++)
            _db.PortfolioSnapshots.Add(new PortfolioSnapshot
            {
                Date = DateTime.UtcNow.Date.AddDays(-values.Length + i),
                TotalUsd = values[i],
            });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _controller.GetMetrics());
        var value = ok.Value!;
        var maxDd = (double)value.GetType().GetProperty("maxDrawdownPct")!.GetValue(value)!;
        // Peak 1200, trough 600 → drawdown = 50%
        Assert.Equal(50.0, maxDd, 1);
    }

    // ── GetRollingPnl ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetRollingPnl_NoSnapshots_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(await _controller.GetRollingPnl());
        var list = (System.Collections.IEnumerable)ok.Value!;
        Assert.Empty(list.Cast<object>());
    }

    [Fact]
    public async Task GetRollingPnl_BaselineIsFirstSnapshot()
    {
        _db.PortfolioSnapshots.AddRange(
            new PortfolioSnapshot { Date = DateTime.UtcNow.Date.AddDays(-2), TotalUsd = 1000m },
            new PortfolioSnapshot { Date = DateTime.UtcNow.Date.AddDays(-1), TotalUsd = 1100m },
            new PortfolioSnapshot { Date = DateTime.UtcNow.Date,             TotalUsd = 1200m }
        );
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _controller.GetRollingPnl());
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Equal(3, list.Count);

        // First entry baseline → pnlUsd = 0
        var firstPnl = (double)list[0].GetType().GetProperty("pnlUsd")!.GetValue(list[0])!;
        Assert.Equal(0.0, firstPnl);

        // Last entry → pnl = 1200-1000 = 200
        var lastPnl = (double)list[^1].GetType().GetProperty("pnlUsd")!.GetValue(list[^1])!;
        Assert.Equal(200.0, lastPnl);

        // Last entry pct = (1200-1000)/1000 = 20%
        var lastPct = (double)list[^1].GetType().GetProperty("pnlPct")!.GetValue(list[^1])!;
        Assert.Equal(20.0, lastPct, 2);
    }

    // ── TakeSnapshot ────────────────────────────────────────────────────────

    [Fact]
    public async Task TakeSnapshot_NoBalances_StoresZeroes()
    {
        var ok = Assert.IsType<OkObjectResult>(await _controller.TakeSnapshot());
        var totalUsd = (decimal)ok.Value!.GetType().GetProperty("totalUsd")!.GetValue(ok.Value)!;
        Assert.Equal(0m, totalUsd);

        var stored = await _db.PortfolioSnapshots.FindAsync(DateTime.UtcNow.Date);
        Assert.NotNull(stored);
        Assert.Equal(0m, stored!.TotalUsd);
    }

    [Fact]
    public async Task TakeSnapshot_WithBalances_SumsLatestValue()
    {
        _state.Balances["BTC"] = new BalanceDto { Asset = "BTC", LatestValue = 50000m, LatestValueGbp = 40000m };
        _state.Balances["ETH"] = new BalanceDto { Asset = "ETH", LatestValue = 10000m, LatestValueGbp = 8000m };

        var ok = Assert.IsType<OkObjectResult>(await _controller.TakeSnapshot());
        var totalUsd = (decimal)ok.Value!.GetType().GetProperty("totalUsd")!.GetValue(ok.Value)!;
        var totalGbp = (decimal)ok.Value!.GetType().GetProperty("totalGbp")!.GetValue(ok.Value)!;
        Assert.Equal(60000m, totalUsd);
        Assert.Equal(48000m, totalGbp);
    }

    [Fact]
    public async Task TakeSnapshot_TwiceOnSameDay_UpdatesExisting()
    {
        _state.Balances["USD"] = new BalanceDto { Asset = "USD", LatestValue = 100m, LatestValueGbp = 80m };
        await _controller.TakeSnapshot();

        // Increase balance and re-snapshot
        _state.Balances["USD"] = new BalanceDto { Asset = "USD", LatestValue = 999m, LatestValueGbp = 800m };
        await _controller.TakeSnapshot();

        var allSnapshots = await _db.PortfolioSnapshots.ToListAsync();
        Assert.Single(allSnapshots);
        Assert.Equal(999m, allSnapshots[0].TotalUsd);
    }
}
