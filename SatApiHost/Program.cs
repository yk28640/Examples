using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Siemens.Automation.AutomationTool.API;
using IResult = Microsoft.AspNetCore.Http.IResult;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5737));
var app = builder.Build();
var token = Environment.GetEnvironmentVariable("SERVICEV14_SAT_TOKEN");
Environment.SetEnvironmentVariable("SERVICEV14_SAT_TOKEN", null);

app.MapGet("/health", (HttpContext context) =>
{
    if (token is not null && context.Request.Headers.TryGetValue("X-Sat-Challenge", out var values))
    {
        var challenge = values.ToString();
        if (challenge.Length != 64 || !challenge.All(Uri.IsHexDigit))
            return Results.BadRequest();

        var proof = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.ASCII.GetBytes(challenge));
        context.Response.Headers["X-Sat-Proof"] = Convert.ToBase64String(proof);
        context.Response.Headers.CacheControl = "no-store";
    }

    return Results.Ok(new { status = "ok" });
});
app.MapGet("/api/sat/adapters", (HttpContext context) =>
{
    if (token is not null && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(context.Request.Headers["X-Sat-Token"].ToString())))
        return Results.Unauthorized();

    return SatAdapters.List(app.Logger);
});

app.Run();

internal static class SatAdapters
{
    private static readonly object SdkLock = new();

    public static IResult List(ILogger logger)
    {
        try
        {
            return Query(logger);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SAT adapter enumeration failed.");
            return Unavailable();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IResult Query(ILogger logger)
    {
        lock (SdkLock)
        {
            var result = new Network().QueryNetworkInterfaceCards(out var adapters);
            if (result.Failed)
            {
                logger.LogError("SAT QueryNetworkInterfaceCards failed: {Code}: {Description}",
                    result.Error, result.GetErrorDescription(Language.English));
                return Unavailable();
            }

            var descriptions = result.HasWarnings ? result.GetWarningDescription(Language.English) : null;
            var warnings = result.HasWarnings
                ? result.Warnings.Select((code, index) => new
                {
                    code = code.ToString(),
                    description = descriptions?.ElementAtOrDefault(index) ?? ""
                }).ToArray()
                : [];

            return Results.Ok(new { adapters, warnings });
        }
    }

    private static IResult Unavailable() => Results.Problem(
        title: "SAT adapter enumeration is unavailable.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
