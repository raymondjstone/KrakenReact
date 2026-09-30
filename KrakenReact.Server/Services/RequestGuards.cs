namespace KrakenReact.Server.Services;

/// <summary>
/// Rejects state-changing API calls that don't carry the app's own header.
/// <para>
/// This is what stops another website driving the API through the user's browser (cross-site request forgery). A page
/// on any origin can POST to this server without a CORS pre-check as long as the request is "simple" — and several
/// endpoints, including the one that market-sells an entire balance, take no body, so they are. A request that adds a
/// custom header is never simple: the browser must first ask permission via a CORS pre-flight, and the CORS policy
/// only grants that to this app's own origins. The app's client sends the header on every call; a hostile page can't.
/// </para>
/// No login is involved. GET, HEAD and OPTIONS (pre-flight) pass through untouched.
/// </summary>
public sealed class RequireClientHeaderMiddleware
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "KrakenReact";

    private readonly RequestDelegate _next;
    public RequireClientHeaderMiddleware(RequestDelegate next) => _next = next;

    public static bool NeedsHeader(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

    public async Task InvokeAsync(HttpContext context)
    {
        if (NeedsHeader(context.Request) &&
            !string.Equals(context.Request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = $"Missing {HeaderName} header — request refused." });
            return;
        }
        await _next(context);
    }
}

/// <summary>
/// Optionally restricts which Host names the server answers to. Without it, a hostile site can use DNS rebinding — a
/// name it controls that resolves to this machine — to make the browser treat this server as the hostile site's own
/// origin and so bypass CORS entirely. Enabled by listing names in <c>Security:AllowedHosts</c>
/// (env <c>Security__AllowedHosts=localhost,myhost</c>); empty means allow all, as before.
/// </summary>
public sealed class AllowedHostsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowed;

    public AllowedHostsMiddleware(RequestDelegate next, IEnumerable<string> allowedHosts)
    {
        _next = next;
        _allowed = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
    }

    public static string[] Parse(string? configured) =>
        (configured ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowed.Count > 0 && !_allowed.Contains(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await context.Response.WriteAsync("Host not allowed.");
            return;
        }
        await _next(context);
    }
}
