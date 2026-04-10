using Microsoft.AspNetCore.SignalR;

namespace KrakenReact.Server.Hubs;

public class TradingHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }
}
