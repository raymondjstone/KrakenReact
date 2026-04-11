using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class NormalizeAssetTests
{
    [Theory]
    [InlineData("XXBT", "BTC")]
    [InlineData("XBT", "BTC")]
    [InlineData("XETH", "ETH")]
    [InlineData("XXRP", "XRP")]
    [InlineData("XXLM", "XLM")]
    [InlineData("XLTC", "LTC")]
    [InlineData("XXMR", "XMR")]
    [InlineData("XXDG", "XDG")]
    [InlineData("XZEC", "ZEC")]
    [InlineData("XREP", "REP")]
    [InlineData("XMLN", "MLN")]
    [InlineData("XETC", "ETC")]
    [InlineData("ZUSD", "USD")]
    [InlineData("ZEUR", "EUR")]
    [InlineData("ZGBP", "GBP")]
    [InlineData("ZCAD", "CAD")]
    [InlineData("ZJPY", "JPY")]
    [InlineData("ZAUD", "AUD")]
    [InlineData("ZCHF", "CHF")]
    public void NormalizeAsset_ResolvesKrakenPrefixes(string input, string expected)
    {
        Assert.Equal(expected, TradingStateService.NormalizeAsset(input));
    }

    [Theory]
    [InlineData("BTC", "BTC")]
    [InlineData("ETH", "ETH")]
    [InlineData("SOL", "SOL")]
    [InlineData("USD", "USD")]
    public void NormalizeAsset_PassesThroughAlreadyNormalized(string input, string expected)
    {
        Assert.Equal(expected, TradingStateService.NormalizeAsset(input));
    }

    [Theory]
    [InlineData("XBT.F", "BTC")]
    [InlineData("ETH.S", "ETH")]
    [InlineData("SOL.B", "SOL")]
    [InlineData("DOT.P", "DOT")]
    [InlineData("XXBT.F", "BTC")]
    public void NormalizeAsset_StripsStakingSuffixes(string input, string expected)
    {
        Assert.Equal(expected, TradingStateService.NormalizeAsset(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeAsset_HandlesNullAndEmpty(string? input, string expected)
    {
        Assert.Equal(expected, TradingStateService.NormalizeAsset(input!));
    }
}
