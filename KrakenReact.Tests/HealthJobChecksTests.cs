using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class HealthJobChecksTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static KeyValuePair<string, FailedJobDto> Failed(string id, DateTime failedAt) =>
        new(id, new FailedJobDto { FailedAt = failedAt, ExceptionType = "TimeoutException", ExceptionMessage = "boom" });

    [Fact]
    public void FailedJobs_none_is_ok()
    {
        var (ok, detail) = HealthController.SummariseFailedJobs([], Now);
        Assert.True(ok);
        Assert.Contains("No failed jobs", detail);
    }

    [Fact]
    public void FailedJobs_only_old_failures_is_ok()
    {
        var (ok, _) = HealthController.SummariseFailedJobs([Failed("1", Now.AddDays(-3))], Now);
        Assert.True(ok);
    }

    [Fact]
    public void FailedJobs_recent_failure_fails_and_names_the_exception()
    {
        var (ok, detail) = HealthController.SummariseFailedJobs([Failed("1", Now.AddHours(-1)), Failed("2", Now.AddHours(-2))], Now);
        Assert.False(ok);
        Assert.Contains("2 failed", detail);
        Assert.Contains("TimeoutException boom", detail);
    }

    [Fact]
    public void FailedJobs_full_page_is_reported_as_a_lower_bound()
    {
        var page = Enumerable.Range(0, 50).Select(i => Failed(i.ToString(), Now.AddMinutes(-i)));
        var (_, detail) = HealthController.SummariseFailedJobs(page, Now);
        Assert.StartsWith("50+", detail);
    }

    [Fact]
    public void Minute_before_first_pass_is_ok_only_while_the_app_is_new()
    {
        Assert.True(HealthController.SummariseMinuteCollection(null, 0, Now, TimeSpan.FromMinutes(5)).Ok);
        Assert.False(HealthController.SummariseMinuteCollection(null, 0, Now, TimeSpan.FromHours(2)).Ok);
    }

    [Fact]
    public void Minute_recent_clean_pass_is_ok()
    {
        Assert.True(HealthController.SummariseMinuteCollection(Now.AddMinutes(-10), 0, Now, TimeSpan.FromDays(1)).Ok);
    }

    [Fact]
    public void Minute_stalled_collector_fails()
    {
        var (ok, detail) = HealthController.SummariseMinuteCollection(Now.AddHours(-7), 0, Now, TimeSpan.FromDays(1));
        Assert.False(ok);
        Assert.Contains("cannot be recovered", detail);
    }

    [Fact]
    public void Minute_gap_fails_even_when_recent()
    {
        var (ok, detail) = HealthController.SummariseMinuteCollection(Now.AddMinutes(-10), 3, Now, TimeSpan.FromDays(1));
        Assert.False(ok);
        Assert.Contains("3 pair(s)", detail);
    }

    private static HealthController NewCtrl(Func<IMonitoringApi> api)
    {
        var db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"health-jobs-{Guid.NewGuid()}").Options);
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        return new HealthController(db, state, api);
    }

    private static List<string> CheckNames(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var checks = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("checks")!.GetValue(ok.Value)!;
        return checks.Cast<object>().Select(c => (string)c.GetType().GetProperty("name")!.GetValue(c)!).ToList();
    }

    [Fact]
    public async Task Health_skips_the_jobs_check_when_hangfire_storage_is_unavailable()
    {
        var names = CheckNames(await NewCtrl(() => throw new InvalidOperationException("no storage")).Get());
        Assert.DoesNotContain("Background Jobs", names);
    }

    [Fact]
    public async Task Health_includes_the_jobs_check_when_storage_responds()
    {
        var api = new Mock<IMonitoringApi>();
        api.Setup(a => a.FailedJobs(0, 50)).Returns(new JobList<FailedJobDto>([]));
        var names = CheckNames(await NewCtrl(() => api.Object).Get());
        Assert.Contains("Background Jobs", names);
    }
}
