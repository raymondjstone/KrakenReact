using System.Diagnostics;
using Hangfire;
using Hangfire.SqlServer;
using KrakenReact.Server.Data;
using KrakenReact.Server.Hubs;
using KrakenReact.Server.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Global reference to Vite process for proper cleanup on shutdown
Process? viteProcess = null;

var builder = WebApplication.CreateBuilder(args);
builder.Environment.WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");

// Load local config (not committed to git) for connection strings / secrets
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();

// EF Core
builder.Services.AddDbContextFactory<KrakenDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("EFDB"),
        sql => sql.CommandTimeout(30)));

// Hangfire — uses the same SQL Server database, creates its own [HangFire] schema
var hangfireConnStr = builder.Configuration.GetConnectionString("EFDB")!;
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(hangfireConnStr, new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.Zero,
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));
builder.Services.AddHangfireServer();
builder.Services.AddTransient<DailyPriceRefreshJob>();
builder.Services.AddTransient<PredictionJob>();
builder.Services.AddTransient<StalePredictionRefreshJob>();
builder.Services.AddTransient<PortfolioSnapshotJob>();
builder.Services.AddTransient<DcaJob>();
builder.Services.AddTransient<ScheduledOrderJob>();
builder.Services.AddTransient<StopLossTakeProfitJob>();
builder.Services.AddTransient<DrawdownAlertJob>();
builder.Services.AddTransient<MultiTfPredictionJob>();
builder.Services.AddTransient<BracketMonitorJob>();
builder.Services.AddTransient<SmartRepriceJob>();

// Data access
builder.Services.AddSingleton<DbMethods>();

// Services
builder.Services.AddSingleton<TradingStateService>();
builder.Services.AddSingleton<KrakenRestService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<AutoOrderService>();
builder.Services.AddSingleton<DelistedPriceService>();

// Background services
builder.Services.AddHostedService<BackgroundTaskService>();
builder.Services.AddHostedService<KrakenWebSocketV1Service>();
builder.Services.AddHostedService<KrakenWebSocketV2Service>();

// Controllers + SignalR
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();

// CORS for React dev server + production origins (configurable via "Cors:AllowedOrigins")
var defaultOrigins = new[] { "http://localhost:5173", "http://localhost:3000", "https://awakethekraken", "http://awakethekraken" };
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
var allowedOrigins = defaultOrigins.Concat(configuredOrigins).Distinct().ToArray();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddOpenApi();

var app = builder.Build();

// Automatically detect schema changes and apply migrations or create missing tables
try
{
    AutoMigrationService.EnsureDatabaseSchema(app.Services);
}
catch (Exception ex)
{
    Log.Error(ex, "Error ensuring database schema");
    throw;
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    var clientDir = Path.Combine(app.Environment.ContentRootPath, "..", "krakenreact.client");
    viteProcess = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c npm run dev",
            WorkingDirectory = clientDir,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        }
    };
    viteProcess.Start();
    Log.Information("Started Vite dev server (PID: {ProcessId})", viteProcess.Id);

    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try
        {
            if (viteProcess != null && !viteProcess.HasExited)
            {
                Log.Information("Killing Vite dev server (PID: {ProcessId})", viteProcess.Id);
                viteProcess.Kill(entireProcessTree: true);
                viteProcess.WaitForExit(5000);
                viteProcess.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error killing Vite process");
        }
    });
}

app.UseCors();

// Hangfire dashboard — Authorization = [] allows access from Docker/reverse proxy (no localhost restriction)
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IsReadOnlyFunc = _ => false,
    Authorization = []
});

// Schedule the recurring price download job from the time stored in the database.
// Change the time via Settings → Schedule in the app UI.
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<KrakenDbContext>>();
        using var db = dbFactory.CreateDbContext();
        var timeSetting = db.AppSettings.FirstOrDefault(s => s.Key == "PriceDownloadTime");
        var cron = TimeToCron(timeSetting?.Value ?? "04:00");
        var manager = app.Services.GetRequiredService<IRecurringJobManager>();
        manager.AddOrUpdate<DailyPriceRefreshJob>(
            "daily-price-download",
            job => job.ExecuteAsync(CancellationToken.None),
            cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
        Log.Information("[Hangfire] Daily price download scheduled at {Time} (cron: {Cron})", timeSetting?.Value ?? "04:00", cron);

        // Schedule prediction job from DB setting (default 05:00)
        var predTimeSetting = db.AppSettings.FirstOrDefault(s => s.Key == "PredictionJobTime");
        var predCron = TimeToCron(predTimeSetting?.Value ?? "05:00");
        manager.AddOrUpdate<PredictionJob>(
            "daily-prediction",
            job => job.ExecuteAsync(CancellationToken.None),
            predCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
        Log.Information("[Hangfire] ML prediction job scheduled at {Time} (cron: {Cron})", predTimeSetting?.Value ?? "05:00", predCron);

        // Schedule nightly portfolio snapshot at 23:55 local time
        manager.AddOrUpdate<PortfolioSnapshotJob>(
            "portfolio-snapshot",
            job => job.ExecuteAsync(),
            "55 23 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
        Log.Information("[Hangfire] Portfolio snapshot scheduled at 23:55");

        // Schedule stale-prediction auto-refresh job
        var autoRefreshSetting = db.AppSettings.FirstOrDefault(s => s.Key == "PredictionAutoRefreshIntervalMinutes");
        var autoRefreshMins = autoRefreshSetting != null && int.TryParse(autoRefreshSetting.Value, out var arm) && arm >= 5 ? arm : 15;
        var autoRefreshCron = IntervalToCron(autoRefreshMins);
        manager.AddOrUpdate<StalePredictionRefreshJob>(
            "stale-prediction-refresh",
            job => job.ExecuteAsync(CancellationToken.None),
            autoRefreshCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        Log.Information("[Hangfire] Stale prediction refresh scheduled every {Mins} min (cron: {Cron})", autoRefreshMins, autoRefreshCron);

        // Schedule stop-loss/take-profit check every 5 minutes
        manager.AddOrUpdate<StopLossTakeProfitJob>(
            "stop-loss-take-profit",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        Log.Information("[Hangfire] Stop-loss/take-profit check scheduled every 5 minutes");

        // Schedule daily drawdown alert at 08:00 local time
        manager.AddOrUpdate<DrawdownAlertJob>(
            "drawdown-alert",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
        Log.Information("[Hangfire] Drawdown alert scheduled at 08:00");

        // Auto-cancel stale orders — runs once daily at 09:00 local time
        manager.AddOrUpdate<AutoCancelJob>(
            "auto-cancel-orders",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 9 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
        Log.Information("[Hangfire] Auto-cancel stale orders scheduled at 09:00");

        // Scheduled one-time orders — check every minute
        manager.AddOrUpdate<ScheduledOrderJob>(
            "scheduled-orders",
            job => job.ExecuteAsync(CancellationToken.None),
            "* * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        Log.Information("[Hangfire] Scheduled orders job registered (every minute)");

        // Bracket order monitor — check every minute for parent fill and OCO management
        manager.AddOrUpdate<BracketMonitorJob>(
            "bracket-monitor",
            job => job.ExecuteAsync(CancellationToken.None),
            "* * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        Log.Information("[Hangfire] Bracket monitor job registered (every minute)");

        // Smart limit order repricing — check every 5 minutes
        manager.AddOrUpdate<SmartRepriceJob>(
            "smart-reprice",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        Log.Information("[Hangfire] Smart reprice job registered (every 5 minutes)");

        // Restore rebalance schedule jobs from DB
        var rebalSchedules = db.RebalanceSchedules.Where(s => s.Active).ToList();
        foreach (var sched in rebalSchedules)
        {
            manager.AddOrUpdate<RebalanceJob>(
                $"rebal-{sched.Id}",
                job => job.ExecuteAsync(sched.Id, CancellationToken.None),
                sched.CronExpression);
        }
        Log.Information("[Hangfire] Restored {Count} rebalance schedule(s)", rebalSchedules.Count);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[Hangfire] Failed to schedule Hangfire jobs");
    }
});

static string TimeToCron(string hhmm)
{
    var parts = hhmm.Split(':');
    if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
        && h is >= 0 and <= 23 && m is >= 0 and <= 59)
        return $"{m} {h} * * *";
    return "0 4 * * *";
}

static string IntervalToCron(int intervalMins)
{
    intervalMins = Math.Max(1, intervalMins);
    var minutes = new List<int>();
    for (var m = 1; m < 60; m += intervalMins) minutes.Add(m);
    return $"{string.Join(",", minutes)} * * * *";
}

// Log static file diagnostics
var webRoot = app.Environment.WebRootPath;
var contentRoot = app.Environment.ContentRootPath;
Log.Information("ContentRootPath: {ContentRoot}", contentRoot);
Log.Information("WebRootPath: {WebRoot}", webRoot);
if (!string.IsNullOrEmpty(webRoot) && Directory.Exists(webRoot))
{
    var files = Directory.GetFiles(webRoot, "*", SearchOption.AllDirectories);
    Log.Information("wwwroot contains {Count} files", files.Length);
    foreach (var f in files.Take(10))
        Log.Information("  {File}", f);
}
else
{
    Log.Warning("wwwroot directory does not exist at: {WebRoot}", webRoot);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<TradingHub>("/tradingHub");
app.MapFallbackToFile("index.html");

app.Run();
