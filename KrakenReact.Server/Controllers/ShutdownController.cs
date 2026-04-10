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

    [HttpPost]
    public IActionResult Shutdown()
    {
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
