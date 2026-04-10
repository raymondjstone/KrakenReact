using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AutoTradeController : ControllerBase
{
    private readonly TradingStateService _state;

    public AutoTradeController(TradingStateService state) => _state = state;

    [HttpGet]
    public ActionResult<List<AutoTradeDto>> GetAll()
    {
        return Ok(_state.AutoOrders.Values.ToList());
    }
}
