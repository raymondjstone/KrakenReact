using Kraken.Net.Enums;
using KrakenReact.Server.Models;

namespace KrakenReact.Server.Services;

/// <summary>
/// The order operations the trading jobs need from the exchange. Kept as an interface so the jobs' state machines
/// (placing, confirming, cancelling) can be unit-tested with a fake instead of a live Kraken connection.
/// </summary>
public interface IOrderGateway
{
    /// <summary>Places an order tagged with a userref and, on an ambiguous failure, looks it up before reporting failure.</summary>
    Task<KrakenRestService.PlacementResult> PlaceOrderWithRecoveryAsync(string symbol, OrderSide side, OrderType orderType,
        decimal qty, decimal price, string? clientOrderId = null, bool postOnly = true);

    Task<bool> CancelOrderAsync(string orderId);

    /// <summary>The order as Kraken reports it, or null if it couldn't be fetched.</summary>
    Task<CombinedOrder?> GetOrderInfoAsync(string orderId);
}

/// <summary>Sends push notifications. Abstracted for the same reason as <see cref="IOrderGateway"/>.</summary>
public interface INotifier
{
    Task<bool> Pushover(string title, string text, string sound = Altairis.Pushover.Client.MessageSound.Falling);
}
