using System.Diagnostics;
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

// CORS for React dev server
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
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
