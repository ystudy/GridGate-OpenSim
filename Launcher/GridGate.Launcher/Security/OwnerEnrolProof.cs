using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace GridGate.Launcher.Security;

/// <summary>
/// On a Region Server or a Connected Server the owner signs in with an account kept on someone else's grid, whose admins can set
/// its password. Once the owner's two-factor is on, that doesn't get anyone in: the secret is kept on this computer. Turning it on
/// therefore needs proof that the person can use this computer: the setup link (carried into setup's two-factor step) or a
/// one-time link the command line prints (<see cref="OwnerEnrolLinks"/>). Every other plan and every other role is as before.
/// </summary>
public static class OwnerEnrolRule
{
    /// <summary>True when the owner's account lives on another grid and <paramref name="role"/> is the owner.</summary>
    public static bool Applies(LauncherSettings s, StaffRole role) =>
        role == StaffRole.Owner && s.Plan is OwnerPlan.RegionServer or OwnerPlan.Connected;
}

/// <summary>
/// The one-time link <c>admin-link --two-factor</c> prints: a random token whose SHA-256 is kept in a file under the data folder's
/// security folder, with the owner's principal id and a run-out time. Only someone who can write that folder can make one. A new
/// link replaces the old one. The token is never stored or logged.
/// </summary>
public sealed class OwnerEnrolLinks(string file, TimeProvider time)
{
    public static readonly TimeSpan Lasts = TimeSpan.FromMinutes(15);

    private sealed record LinkFile(string Hash, Guid Principal, DateTimeOffset Expires);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A new link token for <paramref name="principal"/> (base64url), written as its hash; any older link stops working.</summary>
    public string Create(Guid principal)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var entry = new LinkFile(Hash(token), principal, time.GetUtcNow() + Lasts);
        AtomicFile.WriteAllTextCreatingFolder(file, JsonSerializer.Serialize(entry, Json));
        return token;
    }

    /// <summary>
    /// True, and the link is used up, when <paramref name="token"/> is the current link for <paramref name="principal"/> and it
    /// hasn't run out. Anything else (no link, a wrong or late one, someone else's, a damaged file) is false and changes nothing.
    /// </summary>
    public bool Spend(Guid principal, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        LinkFile? entry;
        try
        {
            if (!File.Exists(file)) return false;
            entry = JsonSerializer.Deserialize<LinkFile>(File.ReadAllText(file), Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        if (entry?.Hash is not { Length: 64 } hash) return false;

        var same = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(token)));
        if (!same || entry.Principal != principal || time.GetUtcNow() > entry.Expires) return false;
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // not spent, so not accepted: a link that stays usable must not open the way twice
        }
        return true;
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The proof itself, held for a short time in a cookie after the setup link or a one-time link was used: the owner's principal id
/// and a run-out time, protected with data protection under a purpose of its own. It is good only for that principal and only
/// until it runs out (checked against the launcher's clock), and it is deleted once two-factor is on.
/// </summary>
public sealed class OwnerEnrolProof(IDataProtectionProvider dp, TimeProvider time)
{
    public const string CookieName = "gg-2fa-proof";

    /// <summary>Seals the cookie; changing it ends proofs already handed out.</summary>
    public const string Purpose = "GridGate.OwnerTwoFactorProof.v1";

    /// <summary>How long the proof lasts after setup's avatar step.</summary>
    public static readonly TimeSpan FromSetup = TimeSpan.FromHours(1);

    /// <summary>How long the proof lasts after a one-time link was opened.</summary>
    public static readonly TimeSpan FromLink = TimeSpan.FromMinutes(30);

    private IDataProtector Protector => dp.CreateProtector(Purpose);

    public void Issue(HttpResponse response, Guid principal, TimeSpan lasts)
    {
        var expires = (time.GetUtcNow() + lasts).ToUnixTimeSeconds();
        var payload = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{principal:N}|{expires}"));
        response.Cookies.Append(CookieName, Convert.ToBase64String(Protector.Protect(payload)), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        });
    }

    public bool Has(HttpRequest request, Guid principal)
    {
        if (request.Cookies[CookieName] is not { Length: > 0 } value) return false;
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Protector.Unprotect(Convert.FromBase64String(value)));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
        var parts = text.Split('|');
        return parts.Length == 2
            && Guid.TryParseExact(parts[0], "N", out var who) && who == principal
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            && time.GetUtcNow().ToUnixTimeSeconds() <= expires;
    }

    public static void Clear(HttpResponse response) => response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
}
