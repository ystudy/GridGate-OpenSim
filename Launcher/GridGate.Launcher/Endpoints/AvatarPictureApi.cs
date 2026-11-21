using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Featured;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Security;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// The Avatar tab's picture (spec 2026-11-21-avatar-page-design, "Pictures"): the signed-in resident's own profile picture as a
/// JPEG through <see cref="ParcelPictures"/>. It takes no id, so it can only ever answer with the asker's own; 404 when they
/// have none or there is no guest side.
/// </summary>
public static class AvatarPictureApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/guest/avatar.jpg", async (HttpContext ctx, Func<LauncherSettings> settings, IPublicProfiles profiles,
            ParcelPictures pictures) =>
        {
            if (!AdminRule.HasStaffPages(settings()) || GuestSignIn.Read(ctx.User) is not { } guest) return Results.NotFound();
            var mine = (await profiles.ReadAsync([guest.Principal], ctx.RequestAborted)).Profiles
                .FirstOrDefault(p => p.Id == guest.Principal);
            if (mine is null || mine.ImageId == Guid.Empty) return Results.NotFound();
            var bytes = await pictures.JpegAsync(mine.ImageId, ctx.RequestAborted);
            if (bytes.Length == 0) return Results.NotFound();
            // Only this browser may keep it: the same address is another resident's picture in another browser.
            ctx.Response.Headers.CacheControl = "private, max-age=300";
            ctx.Response.Headers.Vary = "Cookie";
            return Results.Bytes(bytes, "image/jpeg");
        }).RequireAuthorization(AuthSchemes.GuestPolicy);
    }
}
