using KrakenReact.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace KrakenReact.Server.Hubs;

public class TradingHub : Hub
{
    private readonly TradingStateService _state;

    public TradingHub(TradingStateService state)
    {
        _state = state;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        // Live updates are deltas, so a client needs the full lists once to build on. This also fires on every
        // reconnect (a new connection), which heals any deltas missed while offline.
        await Clients.Caller.SendAsync("OrderUpdate", _state.Orders.Values.ToList());
        await Clients.Caller.SendAsync("BalanceUpdate", _state.Balances.Values.ToList());

        // Send current status to newly connected client
        if (!string.IsNullOrEmpty(_state.LastStatusMessage))
        {
            await Clients.Caller.SendAsync("StatusUpdate", _state.LastStatusMessage);
        }
    }

    public Task SubscribeBook(string pair)
    {
        _state.BookPair = pair;
        return Task.CompletedTask;
    }
}
