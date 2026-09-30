using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class DrawdownCalculationTests
{
    private static readonly DateTime D0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static List<(DateTime, decimal)> Series(params decimal[] values) => values.Select((v, i) => (D0.AddDays(i), v)).ToList();

    [Fact]
    public void FallFromThePeak_IsMeasuredAgainstTheLatestValue()
    {
        var s = DrawdownAlertJob.Current(Series(100, 120, 110, 90));
        Assert.Equal(120m, s!.Peak);
        Assert.Equal(90m, s.Latest);
        Assert.Equal(25m, s.CurrentPct);
    }

    [Fact]
    public void ADipThatHasFullyRecovered_IsNotADrawdown()
    {
        // Deepest fall in the window is 40%, but the portfolio is back at a new high: nothing to report today
        var s = DrawdownAlertJob.Current(Series(100, 60, 70, 105));
        Assert.Equal(0m, s!.CurrentPct);
    }

    [Fact]
    public void ANewHigh_IsZeroDrawdown() =>
        Assert.Equal(0m, DrawdownAlertJob.Current(Series(100, 110, 130))!.CurrentPct);

    [Fact]
    public void ZeroSnapshots_AreMissingData_NotACollapse()
    {
        var s = DrawdownAlertJob.Current(Series(100, 0, 105, 0));
        Assert.Equal(0m, s!.CurrentPct);
        Assert.Equal(105m, s.Latest);
    }

    [Fact]
    public void NoUsableData_ReturnsNull() => Assert.Null(DrawdownAlertJob.Current(Series(0, 0)));

    [Theory]
    [InlineData(5, 10, null, false)]     // under the threshold
    [InlineData(12, 10, null, true)]     // first crossing
    [InlineData(12, 10, 11.0, false)]      // still in the same drawdown: no repeat
    [InlineData(15.5, 10, 11.0, false)]    // deeper, but by less than 5 points
    [InlineData(16, 10, 11.0, true)]       // deepened by 5 points: alert again
    public void ShouldAlert_OncePerCrossing_AgainOnlyIfItDeepens(double current, double threshold, double? last, bool expected)
    {
        Assert.Equal(expected, DrawdownAlertJob.ShouldAlert((decimal)current, (decimal)threshold, last == null ? null : (decimal)last));
    }
}

public class DrawdownAlertJobTests : OrderJobTestBase
{
    public DrawdownAlertJobTests()
    {
        State.DrawdownAlertEnabled = true;
        State.DrawdownAlertThreshold = 10m;
    }

    private DrawdownAlertJob NewJob() => new(State, Factory, Notifier.Object, new Mock<ILogger<DrawdownAlertJob>>().Object);

    private async Task Snapshots(params decimal[] values)
    {
        var start = DateTime.UtcNow.Date.AddDays(-values.Length + 1);
        for (var i = 0; i < values.Length; i++)
            await Seed(new PortfolioSnapshot { Date = start.AddDays(i), TotalUsd = values[i], TotalGbp = values[i] });
    }

    private void Alerts(Times t) =>
        Notifier.Verify(n => n.Pushover(It.Is<string>(s => s.Contains("Drawdown")), It.IsAny<string>(), It.IsAny<string>()), t);

    [Fact]
    public async Task CurrentlyBeyondTheThreshold_Alerts()
    {
        await Snapshots(100, 110, 100, 95, 90);          // 18% below the 110 peak
        await NewJob().ExecuteAsync();
        Alerts(Times.Once());
    }

    [Fact]
    public async Task ARecoveredDip_DoesNotAlert()
    {
        await Snapshots(100, 50, 70, 100, 112);          // deep dip long ago, new high today
        await NewJob().ExecuteAsync();
        Alerts(Times.Never());
    }

    [Fact]
    public async Task StayingInTheSameDrawdown_AlertsOnlyOnce()
    {
        await Snapshots(100, 110, 100, 95, 90);
        var job = NewJob();

        await job.ExecuteAsync();
        await job.ExecuteAsync();
        await job.ExecuteAsync();

        Alerts(Times.Once());   // used to be every morning for 90 days
    }

    [Fact]
    public async Task ReArmsAfterRecovering_ThenAlertsOnTheNextFall()
    {
        await Snapshots(100, 110, 100, 95, 90);
        var job = NewJob();
        await job.ExecuteAsync();                                     // alerts, remembers

        await using (var db = Factory.CreateDbContext())
        {
            db.PortfolioSnapshots.Add(new PortfolioSnapshot { Date = DateTime.UtcNow.Date.AddDays(1), TotalUsd = 115m });   // back to a new high
            await db.SaveChangesAsync();
        }
        await job.ExecuteAsync();                                     // under threshold: re-arms quietly
        Alerts(Times.Once());

        await using (var db = Factory.CreateDbContext())
        {
            db.PortfolioSnapshots.Add(new PortfolioSnapshot { Date = DateTime.UtcNow.Date.AddDays(2), TotalUsd = 90m });    // falls again
            await db.SaveChangesAsync();
        }
        await job.ExecuteAsync();
        Alerts(Times.Exactly(2));
    }

    [Fact]
    public async Task TooFewSnapshots_DoesNothing()
    {
        await Snapshots(100, 50);
        await NewJob().ExecuteAsync();
        Alerts(Times.Never());
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        State.DrawdownAlertEnabled = false;
        await Snapshots(100, 110, 100, 95, 60);
        await NewJob().ExecuteAsync();
        Alerts(Times.Never());
    }
}

public class PortfolioSnapshotJobTests : OrderJobTestBase
{
    private PortfolioSnapshotJob NewJob() => new(State, Factory, new Mock<ILogger<PortfolioSnapshotJob>>().Object);

    private void Holding(decimal value) =>
        State.Balances["BTC"] = new BalanceDto { Asset = "BTC", Total = 1m, LatestValue = value, LatestValueGbp = value * 0.8m };

    private async Task<List<PortfolioSnapshot>> Snapshots()
    {
        await using var db = Factory.CreateDbContext();
        return await db.PortfolioSnapshots.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task RecordsTodaysValue()
    {
        Holding(1000m);
        await NewJob().ExecuteAsync();

        var s = Assert.Single(await Snapshots());
        Assert.Equal(1000m, s.TotalUsd);
        Assert.Equal(DateTime.UtcNow.Date, s.Date);
    }

    [Fact]
    public async Task UpdatesTodaysRow_RatherThanAddingASecond()
    {
        Holding(1000m);
        await NewJob().ExecuteAsync();
        Holding(1200m);
        await NewJob().ExecuteAsync();

        Assert.Equal(1200m, Assert.Single(await Snapshots()).TotalUsd);
    }

    [Fact]
    public async Task AnEmptyPortfolio_DoesNotOverwriteAGoodSnapshotWithZero()
    {
        Holding(1000m);
        await NewJob().ExecuteAsync();

        State.Balances.Clear();                   // e.g. just after a restart, before balances load
        await NewJob().ExecuteAsync();

        Assert.Equal(1000m, Assert.Single(await Snapshots()).TotalUsd);
    }

    [Fact]
    public async Task WithAStalePriceFeed_NothingIsRecorded()
    {
        State.MarkFeedTick(DateTime.UtcNow.AddMinutes(-30));
        Holding(1000m);

        await NewJob().ExecuteAsync();

        Assert.Empty(await Snapshots());
    }
}
