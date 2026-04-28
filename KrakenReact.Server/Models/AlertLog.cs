namespace KrakenReact.Server.Models;

public class AlertLog
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Type { get; set; } = "info"; // info | warning | error
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
