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

        // Send current status to newly connected client
        if (!string.IsNullOrEmpty(_state.LastStatusMessage))
        {
            await Clients.Caller.SendAsync("StatusUpdate", _state.LastStatusMessage);
        }
    }
}
