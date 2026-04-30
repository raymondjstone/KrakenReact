using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SymbolsController : ControllerBase
{
    private readonly TradingStateService _state;

    public SymbolsController(TradingStateService state) => _state = state;

    [HttpGet]
    public ActionResult<List<object>> GetAll()
    {
        var symbols = _state.Symbols.Values
            .Where(s => TradingStateService.BaseCurrencies.Contains(s.QuoteAsset))
            .OrderBy(s => s.WebsocketName)
            .Select(s => new
            {
                s.WebsocketName,
                s.BaseAsset,
                s.QuoteAsset,
                s.Status,
                s.OrderMin,
                s.MinValue,
                s.TickSize,
                s.PriceDecimals,
                s.LotDecimals
            })
            .ToList();
        return Ok(symbols);
    }
}
