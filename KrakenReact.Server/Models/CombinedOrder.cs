namespace KrakenReact.Server.Models;

public class CombinedOrder
{
    public string Id { get; set; }
    public string ReferenceId { get; set; }
    public uint? UserReference { get; set; }
    public string? ClientOrderId { get; set; }
    public Kraken.Net.Enums.OrderStatus Status { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? ExpireTime { get; set; }
    public DateTime? CloseTime { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuantityFilled { get; set; }
    public decimal QuoteQuantityFilled { get; set; }
    public decimal Fee { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal StopPrice { get; set; }
    public decimal Price { get; set; }
    public string Misc { get; set; }
    public string Oflags { get; set; }
    public string Reason { get; set; }
    public bool? Margin { get; set; }
    public IEnumerable<string> TradeIds { get; set; }
    public string Symbol { get; set; } = "";
    public Kraken.Net.Enums.OrderSide Side { get; set; }
    public Kraken.Net.Enums.OrderType Type { get; set; }
    public decimal OrderDetailsPrice { get; set; }
    public decimal SecondaryPrice { get; set; }
    public string Leverage { get; set; }
    public string Order { get; set; }
    public string Close { get; set; }

    public CombinedOrder() { }

    public CombinedOrder(Kraken.Net.Objects.Models.KrakenOrder order)
    {
        Id = order.Id;
        ReferenceId = order.ReferenceId;
        UserReference = order.UserReference;
        ClientOrderId = order.ClientOrderId;
        Status = order.Status;
        CreateTime = order.CreateTime;
        StartTime = order.StartTime;
        ExpireTime = order.ExpireTime;
        CloseTime = order.CloseTime;
        Quantity = order.Quantity;
        QuantityFilled = order.QuantityFilled;
        QuoteQuantityFilled = order.QuoteQuantityFilled;
        Fee = order.Fee;
        AveragePrice = order.AveragePrice;
        StopPrice = order.StopPrice;
        Price = order.Price;
        Misc = order.Misc;
        Oflags = order.Oflags;
        Reason = order.Reason;
        Margin = order.Margin;
        TradeIds = order.TradeIds;
        if (order.OrderDetails != null)
        {
            Symbol = order.OrderDetails.Symbol;
            Side = order.OrderDetails.Side;
            Type = order.OrderDetails.Type;
            OrderDetailsPrice = order.OrderDetails.Price;
            if (Price == 0m) Price = OrderDetailsPrice;
            SecondaryPrice = order.OrderDetails.SecondaryPrice;
            Leverage = order.OrderDetails.Leverage;
            Order = order.OrderDetails.Order;
            Close = order.OrderDetails.Close;
        }
    }
}
