using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LedgerController : ControllerBase
{
    private readonly DbMethods _db;

    public LedgerController(DbMethods db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<List<LedgerDto>>> GetAll()
    {
        var ledgers = await _db.GetLedgersAsync();
        return Ok(ledgers.Select(l => new LedgerDto
        {
            Id = l.Id, ReferenceId = l.ReferenceId, Timestamp = l.Timestamp,
            Type = l.Type.ToString(), SubType = l.SubType, Asset = l.Asset,
            Quantity = l.Quantity, Fee = l.Fee, BalanceAfter = l.BalanceAfter,
            FeePercentage = l.BalanceAfter == 0 ? 0 : Math.Round(l.Fee / (l.BalanceAfter + l.Fee) * 100, 2),
            AssetClass = l.AssetClass
        }).ToList());
    }
}
