namespace KrakenReact.Server.Models;

public class PriceSnapshot
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public decimal Price { get; set; }
    public DateTime CapturedAt { get; set; }
}
