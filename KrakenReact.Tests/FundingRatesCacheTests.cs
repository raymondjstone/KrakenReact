using System.Net;
using System.Text;
using KrakenReact.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KrakenReact.Tests;

public class FundingRatesCacheTests
{
    private sealed class CountingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            const string body = """{"tickers":[{"tag":"perpetual","symbol":"PI_XBTUSD","fundingRate":0.0001,"markPrice":100,"indexPrice":99,"last":100},{"tag":"month","symbol":"FI_XBTUSD_260101","fundingRate":0}]}""";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static FundingRatesController Make(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new FundingRatesController(factory.Object, NullLogger<FundingRatesController>.Instance);
    }

    public FundingRatesCacheTests() => FundingRatesController.Cache.Clear();

    [Fact]
    public async Task RepeatRequests_WithinTheWindow_CostOneUpstreamCall()
    {
        var handler = new CountingHandler();
        var controller = Make(handler);

        var first = Assert.IsType<OkObjectResult>(await controller.GetFundingRates(default));
        var second = Assert.IsType<OkObjectResult>(await controller.GetFundingRates(default));
        var third = Assert.IsType<OkObjectResult>(await Make(handler).GetFundingRates(default));   // a different tab / request

        Assert.Equal(1, handler.Calls);
        Assert.Single(Assert.IsType<List<object>>(first.Value));       // only the perpetual
        Assert.Same(first.Value, second.Value);
        Assert.Same(first.Value, third.Value);
    }

    [Fact]
    public async Task AnUpstreamError_IsNotCached()
    {
        var failing = new CountingHandler(HttpStatusCode.BadGateway);
        var result = await Make(failing).GetFundingRates(default);
        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);

        var working = new CountingHandler();
        Assert.IsType<OkObjectResult>(await Make(working).GetFundingRates(default));   // the failure did not stick
        Assert.Equal(1, working.Calls);
    }
}
