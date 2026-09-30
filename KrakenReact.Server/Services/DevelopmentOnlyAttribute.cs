using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;

namespace KrakenReact.Server.Services;

/// <summary>
/// Restricts a diagnostic endpoint to the Development environment; anywhere else it answers 404 as if it did not exist.
/// Diagnostics expose internals (balances, symbol maps) or make many exchange calls per request, which a deployed instance -
/// with no login in front of it - should not offer. A resource filter, so no constructor injection changes are needed.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class DevelopmentOnlyAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var env = context.HttpContext.RequestServices.GetService(typeof(IHostEnvironment)) as IHostEnvironment;
        if (env == null || !env.IsDevelopment())
            context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
