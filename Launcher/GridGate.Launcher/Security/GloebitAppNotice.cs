using System.Globalization;
using System.Security.Claims;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Money;

namespace GridGate.Launcher.Security;

/// <summary>
/// The dashboard notice on a Region Server when its Gloebit app changes: a line in the audit log (action
/// <see cref="Action"/>, the masked key as its target) when the app is saved or dropped, and the newest such line shown for
/// <see cref="ShownFor"/>. The key is only ever written as its last four characters; the secret never is.
/// </summary>
public static class GloebitAppNotice
{
    public const string Action = "gloebit-app-changed";

    public static readonly TimeSpan ShownFor = TimeSpan.FromDays(30);

    /// <summary>Records that <paramref name="actor"/> saved the app with <paramref name="key"/>.</summary>
    public static void Saved(AuditLog audit, ClaimsPrincipal actor, string? ip, string key) =>
        audit.Write(actor, ip, Action, GloebitRules.Masked(key), "saved");

    /// <summary>Records that the app with <paramref name="key"/> was dropped (a re-point to another grid does that).</summary>
    public static void Removed(AuditLog audit, ClaimsPrincipal actor, string? ip, string key) =>
        audit.Write(actor, ip, Action, GloebitRules.Masked(key), "removed");

    /// <summary>The notice's words for the newest change within <see cref="ShownFor"/>, or null.</summary>
    public static string? Latest(AuditLog audit, DateTimeOffset now)
    {
        var found = audit.Recent(e => e.Entry.Action == Action, 1);
        if (found.Count == 0 || now - found[0].At > ShownFor) return null;
        var (entry, at) = (found[0].Entry, found[0].At);
        var when = at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        return entry.Result == "removed"
            ? $"This computer's Gloebit app (key {entry.Target}) was removed on {when}. Its regions refuse payments until a Gloebit app is saved."
            : $"This computer's Gloebit app was changed on {when}: key {entry.Target}.";
    }
}
