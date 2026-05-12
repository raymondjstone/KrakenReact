using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class NotificationService
{
    private readonly DbMethods _db;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private const int MaxAlertLogRows = 500;

    public NotificationService(DbMethods db, IDbContextFactory<KrakenDbContext> dbFactory)
    {
        _db = db;
        _dbFactory = dbFactory;
    }

    public async Task<bool> Pushover(string title, string text, string sound = Altairis.Pushover.Client.MessageSound.Falling)
    {
        await LogAlert(title, text, "info");

        try
        {
            var p = await _db.GetPushoverCredentialsAsync();
            if (p != null)
            {
                var client = new Altairis.Pushover.Client.PushoverClient(p.appsecret);
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
            Console.WriteLine($"Pushover notification failed: {ex.Message}");
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

            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM [AlertLogs] WHERE [Id] NOT IN (SELECT TOP({MaxAlertLogRows}) [Id] FROM [AlertLogs] ORDER BY [CreatedAt] DESC)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Alert log failed: {ex.Message}");
        }
    }
}
