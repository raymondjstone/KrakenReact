namespace KrakenReact.Server.Services;

/// <summary>
/// A decimal that can be read and written from several threads without tearing. A <c>decimal</c> is 16 bytes and the runtime does
/// not promise a plain read or write of one is atomic, so a job reading a setting while the settings page saves it could see half
/// the old value and half the new one - a threshold that was never configured. Reads and writes are tiny, so a lock is enough.
/// </summary>
public sealed class AtomicDecimal
{
    private readonly object _gate = new();
    private decimal _value;

    public AtomicDecimal(decimal initial = 0m) => _value = initial;

    public decimal Value
    {
        get { lock (_gate) return _value; }
        set { lock (_gate) _value = value; }
    }
}
