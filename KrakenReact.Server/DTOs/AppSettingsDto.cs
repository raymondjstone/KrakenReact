namespace KrakenReact.Server.DTOs;

public class AppSettingsDto
{
    public string? KrakenApiKey { get; set; }
    public string? KrakenApiSecret { get; set; }
    public string? PushoverUserKey { get; set; }
    public string? PushoverApiToken { get; set; }
    public bool StakingNotifications { get; set; }
    public bool HideAlmostZeroBalances { get; set; }
    public bool OrderProximityNotifications { get; set; }
    public decimal OrderProximityThreshold { get; set; }
    public string Theme { get; set; } = "dark";
    public List<string> BaseCurrencies { get; set; } = new();
    public List<string> Blacklist { get; set; } = new();
    public List<string> MajorCoin { get; set; } = new();
    public List<string> Currency { get; set; } = new();
    public List<string> BadPairs { get; set; } = new();
    public List<string> DefaultPairs { get; set; } = new();
    public Dictionary<string, string> AssetNormalizations { get; set; } = new();
    public List<decimal> OrderPriceOffsets { get; set; } = new();
    public List<decimal> OrderQtyPercentages { get; set; } = new();
    public bool AutoSellOnBuyFill { get; set; }
    public decimal AutoSellPercentage { get; set; }
    public bool AutoAddStakingToOrder { get; set; }
    public int OrderBookDepth { get; set; }
}

public class SaveSettingsRequest
{
    public string? KrakenApiKey { get; set; }
    public string? KrakenApiSecret { get; set; }
    public string? PushoverUserKey { get; set; }
    public string? PushoverApiToken { get; set; }
    public bool? StakingNotifications { get; set; }
    public bool? HideAlmostZeroBalances { get; set; }
    public bool? OrderProximityNotifications { get; set; }
    public decimal? OrderProximityThreshold { get; set; }
    public string? Theme { get; set; }
    public List<string>? BaseCurrencies { get; set; }
    public List<string>? Blacklist { get; set; }
    public List<string>? MajorCoin { get; set; }
    public List<string>? Currency { get; set; }
    public List<string>? BadPairs { get; set; }
    public List<string>? DefaultPairs { get; set; }
    public Dictionary<string, string>? AssetNormalizations { get; set; }
    public List<decimal>? OrderPriceOffsets { get; set; }
    public List<decimal>? OrderQtyPercentages { get; set; }
    public bool? AutoSellOnBuyFill { get; set; }
    public decimal? AutoSellPercentage { get; set; }
    public bool? AutoAddStakingToOrder { get; set; }
    public int? OrderBookDepth { get; set; }
}
