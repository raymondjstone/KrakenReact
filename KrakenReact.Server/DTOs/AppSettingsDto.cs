namespace KrakenReact.Server.DTOs;

public class AppSettingsDto
{
    public string? KrakenApiKey { get; set; }
    public string? KrakenApiSecret { get; set; }
    public string? PushoverUserKey { get; set; }
    public string? PushoverApiToken { get; set; }
    public bool StakingNotifications { get; set; }
    public List<string> BaseCurrencies { get; set; } = new();
    public List<string> Blacklist { get; set; } = new();
    public List<string> MajorCoin { get; set; } = new();
    public List<string> Currency { get; set; } = new();
    public List<string> BadPairs { get; set; } = new();
    public List<string> DefaultPairs { get; set; } = new();
    public Dictionary<string, string> AssetNormalizations { get; set; } = new();
}

public class SaveSettingsRequest
{
    public string? KrakenApiKey { get; set; }
    public string? KrakenApiSecret { get; set; }
    public string? PushoverUserKey { get; set; }
    public string? PushoverApiToken { get; set; }
    public bool? StakingNotifications { get; set; }
    public List<string>? BaseCurrencies { get; set; }
    public List<string>? Blacklist { get; set; }
    public List<string>? MajorCoin { get; set; }
    public List<string>? Currency { get; set; }
    public List<string>? BadPairs { get; set; }
    public List<string>? DefaultPairs { get; set; }
    public Dictionary<string, string>? AssetNormalizations { get; set; }
}
