using KrakenReact.Server.Data;

namespace KrakenReact.Server.Services;

public class NotificationService
{
    private readonly DbMethods _db;

    public NotificationService(DbMethods db) => _db = db;

    public async Task<bool> Pushover(string title, string text, string sound = Altairis.Pushover.Client.MessageSound.Falling)
    {
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
}
