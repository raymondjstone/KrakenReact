using System.Reflection;
using KrakenReact.Server.Controllers;

namespace KrakenReact.Tests;

public class BalancesControllerTests
{
    // GetQuoteCurrency is private static — test it via reflection since it contains important logic
    private static string InvokeGetQuoteCurrency(string symbol)
    {
        var method = typeof(BalancesController).GetMethod("GetQuoteCurrency", BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, [symbol])!;
    }

    [Theory]
    [InlineData("XBT/USD", "USD")]
    [InlineData("ETH/GBP", "GBP")]
    [InlineData("SOL/EUR", "EUR")]
    [InlineData("DOT/USDT", "USDT")]
    public void GetQuoteCurrency_SlashedPair_ReturnsQuote(string symbol, string expected)
    {
        Assert.Equal(expected, InvokeGetQuoteCurrency(symbol));
    }

    [Theory]
    [InlineData("XBTUSD", "USD")]
    [InlineData("ETHUSDT", "USDT")]
    [InlineData("SOLUSDC", "USDC")]
    [InlineData("DOTGBP", "GBP")]
    [InlineData("ADAEUR", "EUR")]
    public void GetQuoteCurrency_NoSlash_ExtractsFromSuffix(string symbol, string expected)
    {
        Assert.Equal(expected, InvokeGetQuoteCurrency(symbol));
    }

    [Theory]
    [InlineData("", "USD")]
    [InlineData(null, "USD")]
    public void GetQuoteCurrency_NullOrEmpty_DefaultsToUSD(string? symbol, string expected)
    {
        Assert.Equal(expected, InvokeGetQuoteCurrency(symbol!));
    }

    [Fact]
    public void GetQuoteCurrency_UnknownSuffix_DefaultsToUSD()
    {
        Assert.Equal("USD", InvokeGetQuoteCurrency("SOLJPY"));
    }
}
