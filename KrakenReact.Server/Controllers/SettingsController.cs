using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(KrakenDbContext db, TradingStateService state, ILogger<SettingsController> logger)
    {
        _db = db;
        _state = state;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<AppSettingsDto>> GetSettings()
    {
        try
        {
            // After initialization, all settings should exist in database
            var settings = new AppSettingsDto
            {
                BaseCurrencies = await GetSettingList("BaseCurrencies") ?? new List<string>(),
                Blacklist = await GetSettingList("Blacklist") ?? new List<string>(),
                MajorCoin = await GetSettingList("MajorCoin") ?? new List<string>(),
                Currency = await GetSettingList("Currency") ?? new List<string>(),
                BadPairs = await GetSettingList("BadPairs") ?? new List<string>(),
                DefaultPairs = await GetSettingList("DefaultPairs") ?? new List<string>(),
                AssetNormalizations = await _db.AssetNormalizations.ToDictionaryAsync(a => a.KrakenName, a => a.NormalizedName)
            };

            // Get Kraken API keys
            var krakenApiKey = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "KrakenApiKey");
            var krakenApiSecret = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "KrakenApiSecret");
            settings.KrakenApiKey = krakenApiKey?.Value ?? "";
            settings.KrakenApiSecret = krakenApiSecret != null ? MaskSecret(krakenApiSecret.Value) : "";

            // Get Pushover keys
            var pushoverUser = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PushoverUserKey");
            var pushoverToken = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PushoverAppToken");
            settings.PushoverUserKey = pushoverUser != null ? MaskSecret(pushoverUser.Value) : "";
            settings.PushoverApiToken = pushoverToken != null ? MaskSecret(pushoverToken.Value) : "";

            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings");
            return StatusCode(500, "Error loading settings");
        }
    }

    [HttpPost]
    public async Task<ActionResult> SaveSettings([FromBody] SaveSettingsRequest request)
    {
        try
        {
            // Save Kraken API keys if provided (save to new AppSettings table)
            if (!string.IsNullOrEmpty(request.KrakenApiKey) && !request.KrakenApiKey.Contains("***"))
            {
                await SaveOrUpdateSetting("KrakenApiKey", request.KrakenApiKey, "Kraken API Key");
            }
            if (!string.IsNullOrEmpty(request.KrakenApiSecret) && !request.KrakenApiSecret.Contains("***"))
            {
                await SaveOrUpdateSetting("KrakenApiSecret", request.KrakenApiSecret, "Kraken API Secret");
            }

            // Save Pushover keys if provided
            if (!string.IsNullOrEmpty(request.PushoverUserKey) && !request.PushoverUserKey.Contains("***"))
            {
                await SaveOrUpdateSetting("PushoverUserKey", request.PushoverUserKey, "Pushover User Key");
            }
            if (!string.IsNullOrEmpty(request.PushoverApiToken) && !request.PushoverApiToken.Contains("***"))
            {
                await SaveOrUpdateSetting("PushoverAppToken", request.PushoverApiToken, "Pushover App Token");
            }

            // Save lists
            if (request.BaseCurrencies != null)
                await SaveSettingList("BaseCurrencies", request.BaseCurrencies);
            if (request.Blacklist != null)
                await SaveSettingList("Blacklist", request.Blacklist);
            if (request.MajorCoin != null)
                await SaveSettingList("MajorCoin", request.MajorCoin);
            if (request.Currency != null)
                await SaveSettingList("Currency", request.Currency);
            if (request.BadPairs != null)
                await SaveSettingList("BadPairs", request.BadPairs);
            if (request.DefaultPairs != null)
                await SaveSettingList("DefaultPairs", request.DefaultPairs);

            // Save asset normalizations
            if (request.AssetNormalizations != null)
            {
                var existing = await _db.AssetNormalizations.ToListAsync();
                _db.AssetNormalizations.RemoveRange(existing);
                foreach (var kvp in request.AssetNormalizations)
                {
                    _db.AssetNormalizations.Add(new AssetNormalization
                    {
                        KrakenName = kvp.Key,
                        NormalizedName = kvp.Value
                    });
                }
            }

            await _db.SaveChangesAsync();

            // Reload configuration in TradingStateService
            await _state.ReloadConfiguration(_db);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            return StatusCode(500, "Error saving settings");
        }
    }

    private async Task<List<string>?> GetSettingList(string key)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null) return null;
        return setting.Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    private async Task SaveSettingList(string key, List<string> values)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            setting = new AppSettings { Key = key };
            _db.AppSettings.Add(setting);
        }
        setting.Value = string.Join(",", values);
    }

    private async Task SaveOrUpdateSetting(string key, string value, string? description = null)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            setting = new AppSettings { Key = key, Description = description };
            _db.AppSettings.Add(setting);
        }
        setting.Value = value;
        if (description != null) setting.Description = description;
    }

    [HttpGet("pinned-pairs")]
    public async Task<ActionResult> GetPinnedPairs()
    {
        var list = await GetSettingList("PinnedPairs");
        return Ok(list ?? new List<string> { "XBT/USD", "ETH/USD", "SOL/USD" });
    }

    [HttpPut("pinned-pairs")]
    public async Task<ActionResult> SavePinnedPairs([FromBody] List<string> pairs)
    {
        await SaveSettingList("PinnedPairs", pairs);
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static string MaskSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 8) return "***";
        return secret[..4] + "***" + secret[^4..];
    }
}
