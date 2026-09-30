using KrakenReact.Server.Controllers;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System.Reflection;

namespace KrakenReact.Tests;

public class DevelopmentOnlyTests
{
    private static IActionResult? Run(string? environment)
    {
        var services = new ServiceCollection();
        if (environment != null)
        {
            var env = new Mock<IHostEnvironment>();
            env.SetupGet(e => e.EnvironmentName).Returns(environment);
            services.AddSingleton(env.Object);
        }
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var ctx = new ResourceExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), new List<IValueProviderFactory>());
        new DevelopmentOnlyAttribute().OnResourceExecuting(ctx);
        return ctx.Result;
    }

    [Fact] public void InDevelopment_TheEndpointIsAvailable() => Assert.Null(Run("Development"));
    [Fact] public void InProduction_TheEndpointIsNotFound() => Assert.IsType<NotFoundResult>(Run("Production"));
    [Fact] public void InAnyOtherEnvironment_TheEndpointIsNotFound() => Assert.IsType<NotFoundResult>(Run("Staging"));
    [Fact] public void WithNoEnvironmentAtAll_TheEndpointIsNotFound() => Assert.IsType<NotFoundResult>(Run(null));

    [Theory]
    [InlineData(typeof(OrdersController), "GetDebug")]
    [InlineData(typeof(PricesController), "GetKlinesDebug")]
    [InlineData(typeof(BalancesController), "GetDiagnostics")]
    public void TheDiagnosticEndpoints_AreGated(Type controller, string action)
    {
        var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<DevelopmentOnlyAttribute>());
    }
}
