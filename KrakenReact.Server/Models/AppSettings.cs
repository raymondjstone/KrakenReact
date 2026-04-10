namespace KrakenReact.Server.Models;

/// <summary>
/// Stores application configuration settings that can be modified by users
/// </summary>
public class AppSettings
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Description { get; set; }
}
