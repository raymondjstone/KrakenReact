using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class NotificationService : INotifier
{
    private readonly DbMethods _db;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<NotificationService> _logger;
    private const int MaxAlertLogRows = 500;
    private const int PruneEvery = 20;
    private int _insertsSincePrune;

    private readonly NotificationThrottle _throttle = new();
    private Altairis.Pushover.Client.PushoverClient? _client;
    private string? _clientToken;
    private readonly object _clientLock = new();

    /// <summary>One client reused across messages (a new one per message churns sockets in a burst); rebuilt if the token changes.</summary>
    private Altairis.Pushover.Client.PushoverClient GetClient(string token)
    {
        lock (_clientLock)
        {
            if (_client == null || _clientToken != token)
            {
                _client = new Altairis.Pushover.Client.PushoverClient(token);
                _clientToken = token;
            }
            return _client;
        }
    }

    public NotificationService(DbMethods db, IDbContextFactory<KrakenDbContext> dbFactory, ILogger<NotificationService> logger)
    {
        _db = db;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<bool> Pushover(string title, string text, string sound = Altairis.Pushover.Client.MessageSound.Falling)
    {
        await LogAlert(title, text, "info");

        // Everything is kept in the in-app alert log above; only the phone push is throttled
        if (!_throttle.ShouldSend(title, text))
        {
            _logger.LogInformation("Pushover suppressed (duplicate or rate limit): {Title}", title);
            return false;
        }

        try
        {
            var p = await _db.GetPushoverCredentialsAsync();
            if (p != null)
            {
                var client = GetClient(p.appsecret);
                var message = new Altairis.Pushover.Client.PushoverMessage(p.appkey, text)
                {
                    Title = title,
                    Sound = sound
                };
                var result = await client.SendMessage(message);
                return result.Status;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pushover notification failed");
        }
        return false;
    }

    public async Task LogAlert(string title, string text, string type = "info")
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Raw INSERT avoids the EF MERGE that auto-increment Id forces (acquires UPDATE lock table-wide)
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [AlertLogs] ([Title],[Text],[Type],[CreatedAt]) VALUES ({0},{1},{2},{3})",
                title, text, type, DateTime.UtcNow);

            // Trim only every PruneEvery-th insert — the log is allowed to overshoot slightly, and this avoids a
            // second statement (with a sort) on every single alert.
            if (Interlocked.Increment(ref _insertsSincePrune) % PruneEvery == 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM [AlertLogs] WHERE [Id] NOT IN (SELECT TOP({0}) [Id] FROM [AlertLogs] ORDER BY [CreatedAt] DESC)",
                    MaxAlertLogRows);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert log write failed");
        }
    }
}
