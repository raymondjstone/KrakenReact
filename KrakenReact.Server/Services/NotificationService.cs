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
            db.AlertLogs.Add(new AlertLog
            {
                Title = title,
                Text = text,
                Type = type,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            // Trim old rows to keep the table bounded.
            // Single DELETE statement keeps the transaction short; the old pattern of
            // RemoveRange + SaveChangesAsync generated N individual DELETEs in one
            // transaction, holding write locks long enough to block concurrent COUNT reads.
            var count = await db.AlertLogs.CountAsync();
            if (count > MaxAlertLogRows)
            {
                int excess = count - MaxAlertLogRows;
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM [AlertLogs] WHERE [Id] IN (SELECT TOP({excess}) [Id] FROM [AlertLogs] ORDER BY [CreatedAt] ASC)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Alert log failed: {ex.Message}");
        }
    }
}
