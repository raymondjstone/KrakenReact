using KrakenReact.Server.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShutdownController : ControllerBase
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<ShutdownController> _logger;

    public ShutdownController(IHostApplicationLifetime applicationLifetime, IHubContext<TradingHub> hub, ILogger<ShutdownController> logger)
    {
        _applicationLifetime = applicationLifetime;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// True when running inside a container (the ASP.NET images set DOTNET_RUNNING_IN_CONTAINER). There the process is
    /// supervised by Docker with a restart policy, so exiting it just makes Docker start it again a moment later.
    /// </summary>
    public static bool InContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the in-app Shutdown button can do anything useful; the UI hides it otherwise.</summary>
    [HttpGet]
    public IActionResult Availability() => Ok(new
    {
        available = !InContainer,
        reason = InContainer ? "Running in a container: stop it with 'docker compose stop' (the restart policy would bring it straight back)." : null,
    });

    [HttpPost]
    public IActionResult Shutdown()
    {
        if (InContainer)
            return Conflict(new { message = "Running in a container: exiting would only make Docker restart it. Use 'docker compose stop' to stop it." });

        _logger.LogWarning("Shutdown requested via API - stopping application...");

        // Notify all connected clients to close, then stop the server
        Task.Run(async () =>
        {
            try { await _hub.Clients.All.SendAsync("AppShutdown"); }
            catch { /* best effort */ }
            await Task.Delay(500); // Give time for response + SignalR message
            _applicationLifetime.StopApplication();
        });

        return Ok(new { message = "Application shutdown initiated" });
    }
}
