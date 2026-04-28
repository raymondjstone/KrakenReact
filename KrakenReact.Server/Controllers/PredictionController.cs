using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/predictions")]
public class PredictionController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly TradingStateService _state;

    public PredictionController(KrakenDbContext db, IBackgroundJobClient backgroundJobClient, TradingStateService state)
    {
        _db = db;
        _backgroundJobClient = backgroundJobClient;
        _state = state;
    }

    /// <summary>GET /api/predictions — latest result per symbol</summary>
    [HttpGet]
    public async Task<IActionResult> GetPredictions()
    {
        var results = await _db.PredictionResults.AsNoTracking().ToListAsync();
        return Ok(results);
    }

    /// <summary>POST /api/predictions/trigger — enqueue the job immediately</summary>
    [HttpPost("trigger")]
    public IActionResult TriggerNow()
    {
        _backgroundJobClient.Enqueue<PredictionJob>(j => j.ExecuteAsync(CancellationToken.None));
        return Ok(new { message = "Prediction job enqueued" });
    }

    /// <summary>POST /api/predictions/trigger/single?symbol=XBT/USD — enqueue for one symbol</summary>
    [HttpPost("trigger/single")]
    public IActionResult TriggerSingle([FromQuery] string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        _backgroundJobClient.Enqueue<PredictionJob>(j => j.ExecuteSingleAsync(symbol, CancellationToken.None));
        return Ok(new { message = $"Prediction enqueued for {symbol}" });
    }

    /// <summary>DELETE /api/predictions?symbol=XBT/USD — remove a prediction result</summary>
    [HttpDelete]
    public async Task<IActionResult> DeletePrediction([FromQuery] string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required" });

        var existing = await _db.PredictionResults.FindAsync(symbol);
        if (existing == null) return NotFound();

        _db.PredictionResults.Remove(existing);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>GET /api/predictions/settings — current prediction config</summary>
    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        var availableCurrencies = _state.Symbols.Values
            .Select(s => s.WebsocketName.Contains('/') ? s.WebsocketName.Split('/')[1] : "")
            .Where(c => c.Length > 0)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        return Ok(new
        {
            symbols             = _state.PredictionSymbols,
            interval            = _state.PredictionInterval,
            mode                = _state.PredictionMode,
            currency            = _state.PredictionCurrency,
            availableCurrencies,
        });
    }
}
