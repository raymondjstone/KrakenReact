namespace KrakenReact.Server.DTOs;

public class OrderDto
{
    public string Id { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuantityFilled { get; set; }
    public decimal Fee { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal OrderValue { get; set; }
    public decimal LatestPrice { get; set; }
    public decimal Distance { get; set; }
    public decimal DistancePercentage { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? CloseTime { get; set; }
    public string Reason { get; set; } = "";
    public string? ClientOrderId { get; set; }
    public decimal SecondaryPrice { get; set; }
    public decimal StopPrice { get; set; }
    public string Leverage { get; set; } = "";
}
