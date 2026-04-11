using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;

namespace KrakenReact.Tests;

public class DtoTests
{
    // --- OrderDto ---

    [Fact]
    public void OrderDto_DefaultValues()
    {
        var order = new OrderDto();
        Assert.Equal("", order.Id);
        Assert.Equal("", order.Symbol);
        Assert.Equal("", order.Side);
        Assert.Equal("", order.Type);
        Assert.Equal("", order.Status);
        Assert.Equal(0m, order.Price);
        Assert.Equal(0m, order.Quantity);
        Assert.Equal(0m, order.QuantityFilled);
        Assert.Null(order.CloseTime);
    }

    // --- BalanceDto ---

    [Fact]
    public void BalanceDto_DefaultValues()
    {
        var bal = new BalanceDto();
        Assert.Equal("", bal.Asset);
        Assert.Equal(0m, bal.Total);
        Assert.Equal(0m, bal.Locked);
        Assert.Equal(0m, bal.Available);
        Assert.Null(bal.TotalCostBasis);
        Assert.Null(bal.NetProfitLoss);
        Assert.Null(bal.NetProfitLossPercentage);
    }

    // --- KlineDto ---

    [Fact]
    public void KlineDto_DefaultValues()
    {
        var kline = new KlineDto();
        Assert.Equal(default, kline.OpenTime);
        Assert.Equal(0m, kline.Open);
        Assert.Equal(0m, kline.Close);
    }

    // --- CreateOrderRequest ---

    [Fact]
    public void CreateOrderRequest_DefaultSide_IsBuy()
    {
        var req = new CreateOrderRequest();
        Assert.Equal("Buy", req.Side);
    }

    // --- PriceDto ---

    [Fact]
    public void PriceDto_DefaultValues()
    {
        var dto = new PriceDto();
        Assert.Equal("", dto.Symbol);
        Assert.Equal("Unknown", dto.Age);
        Assert.Equal("no", dto.KrakenNewPricesLoaded);
        Assert.Null(dto.ClosePrice);
        Assert.Null(dto.ClosePriceMovement);
    }

    // --- DerivedKline ---

    [Fact]
    public void DerivedKline_DefaultConstructor_SetsKey()
    {
        var kline = new DerivedKline();
        Assert.NotNull(kline.Key);
        Assert.Equal("", kline.Asset);
        Assert.Equal("OneMinute", kline.Interval);
    }

    // --- AssetNormalization ---

    [Fact]
    public void AssetNormalization_DefaultValues()
    {
        var norm = new AssetNormalization();
        Assert.Equal("", norm.KrakenName);
        Assert.Equal("", norm.NormalizedName);
    }

    // --- AutoTradeDto ---

    [Fact]
    public void AutoTradeDto_DefaultValues()
    {
        var dto = new AutoTradeDto();
        Assert.Equal("", dto.Symbol);
        Assert.Equal("", dto.Base);
        Assert.False(dto.OrderWanted);
        Assert.False(dto.OrderMade);
        Assert.Equal(0, dto.OrderRanking);
    }
}
