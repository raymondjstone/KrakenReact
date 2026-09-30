using KrakenReact.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace KrakenReact.Server.Hubs;

public class TradingHub : Hub
{
    private readonly TradingStateService _state;
    private readonly BookSubscriptions _books;

    public TradingHub(TradingStateService state, BookSubscriptions books)
    {
        _state = state;
        _books = books;
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

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _books.Release(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task SubscribeBook(string pair)
    {
        if (!_books.Subscribe(Context.ConnectionId, pair))
            throw new HubException("Unknown pair");
        return Task.CompletedTask;
    }

    public Task UnsubscribeBook()
    {
        _books.Release(Context.ConnectionId);
        return Task.CompletedTask;
    }
}
