namespace KrakenReact.Server.Models;

/// <summary>
/// Stores asset normalization rules (e.g., XXBT -> XBT)
/// </summary>
public class AssetNormalization
{
    public string KrakenName { get; set; } = "";
    public string NormalizedName { get; set; } = "";
}
