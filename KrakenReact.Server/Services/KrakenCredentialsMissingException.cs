namespace KrakenReact.Server.Services;

/// <summary>No Kraken API key is saved. Raised instead of the bare NullReferenceException a missing key used to cause, so every log line
/// that reports it says what is actually wrong.</summary>
public sealed class KrakenCredentialsMissingException : InvalidOperationException
{
    public KrakenCredentialsMissingException()
        : base("Kraken API keys are not configured - add them on the Settings page.") { }
}
