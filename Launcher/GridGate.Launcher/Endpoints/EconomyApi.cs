using GridGate.Launcher.Economy;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// The viewer's economy helper (money design §8): <c>POST /economy/currency.php</c> and <c>/economy/landtool.php</c>, open
/// to anyone in every money mode (with money Off it is what lets Firestorm buy free land). XML-RPC in, text/xml out; no
/// cookies, no antiforgery (the viewer has neither).
/// </summary>
public static class EconomyApi
{
    public static void Map(WebApplication app)
    {
        foreach (var path in new[] { EconomyHelper.CurrencyPath, EconomyHelper.LandPath })
            app.MapPost(path, async (HttpContext ctx, EconomyHelper helper) =>
            {
                var xml = await helper.AnswerAsync(path, ctx.Request.Body, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Text(xml, "text/xml", System.Text.Encoding.UTF8);
            }).AllowAnonymous().DisableAntiforgery();
    }
}
