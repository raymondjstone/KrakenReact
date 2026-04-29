namespace KrakenReact.Server.DTOs;

public class OrderLadderRequest
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "Buy";
    public decimal TotalQty { get; set; }
    public decimal StartPrice { get; set; }
    public decimal EndPrice { get; set; }
    public int Count { get; set; } = 5;
}
