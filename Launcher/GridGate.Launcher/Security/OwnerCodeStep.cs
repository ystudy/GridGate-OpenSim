using System.Net;
using System.Security.Claims;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;

namespace GridGate.Launcher.Security;

/// <summary>How the owner's two-factor code check ended, for the Region Server forms that ask for one.</summary>
public enum OwnerCodeResult { Ok, NotSetUp, Unreadable, Locked, WrongCode, SaveFailed }

/// <summary>
/// The owner's current two-factor code, asked for in the same form as a change that moves residents' money: saving a Region
/// Server's Gloebit app and confirming a re-point. On a Region Server the owner is a grid account, which the Grid Asset Server's
/// admins can reset; the two-factor secret is kept on this computer (<see cref="TwoFactorStore"/>), so they can't. A wrong code counts
/// in the sign-in throttle for the owner's name and address, as a wrong code at sign-in does, and locks the same way. The check
/// writes no audit line (each page writes its own, with its own action name). Codes are never logged.
/// </summary>
public static class OwnerCodeStep
{
    /// <summary>Where two-factor is turned on.</summary>
    public const string TurnOnPath = TwoFactorGate.EnrolPath;

    /// <summary>
    /// Whether the signed-in person can be asked for a code now: <see cref="OwnerCodeResult.Ok"/> when two-factor is on,
    /// <see cref="OwnerCodeResult.NotSetUp"/> when it isn't, <see cref="OwnerCodeResult.Unreadable"/> when the two-factor file can't be read.
    /// </summary>
    public static OwnerCodeResult CanAsk(TwoFactorStore store, ClaimsPrincipal user) =>
        Principal(user) is not { } id ? OwnerCodeResult.NotSetUp
            : store.StateOf(id) switch { TwoFactorState.On => OwnerCodeResult.Ok, TwoFactorState.Unknown => OwnerCodeResult.Unreadable, _ => OwnerCodeResult.NotSetUp };

    /// <summary>Whether <paramref name="code"/> is a current code of the signed-in owner; a right code is used up.</summary>
    public static OwnerCodeResult Check(TwoFactorStore store, LoginThrottle throttle, ILogger log, ClaimsPrincipal user, IPAddress? remote, string? code)
    {
        if (Principal(user) is not { } principal) return OwnerCodeResult.NotSetUp;
        var key = AvatarName.Parse(user.Identity?.Name).Key;
        if (throttle.IsLocked(SignInDoor.Staff, key, remote)) return OwnerCodeResult.Locked;
        switch (store.StateOf(principal))
        {
            case TwoFactorState.Off:
                return OwnerCodeResult.NotSetUp;
            case TwoFactorState.Unknown:
                return OwnerCodeResult.Unreadable;
        }

        bool right;
        try
        {
            right = store.VerifyCode(principal, code ?? "", out _);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not a wrong code, so not counted; nothing was used up.
            log.LogWarning("Owner two-factor check: a code couldn't be marked used: {Message}", e.Message);
            return OwnerCodeResult.SaveFailed;
        }
        if (!right)
        {
            throttle.RecordFailure(SignInDoor.Staff, key, remote);
            return OwnerCodeResult.WrongCode;
        }
        throttle.RecordSuccess(SignInDoor.Staff, key);
        return OwnerCodeResult.Ok;
    }

    /// <summary>What the page tells the owner when the check didn't pass.</summary>
    public static string Words(OwnerCodeResult result) => result switch
    {
        OwnerCodeResult.NotSetUp => "This needs two-factor sign-in, which isn't on for your account on this computer. Turn it on first, then come back.",
        OwnerCodeResult.Unreadable => "Your two-factor code can't be checked right now: this computer's two-factor file can't be read.",
        OwnerCodeResult.Locked => "Too many attempts. Try again in a few minutes.",
        OwnerCodeResult.SaveFailed => TwoFactorStore.SaveFailed,
        _ => "That two-factor code isn't right, or it was already used. Wait for your app's next code and try again.",
    };

    /// <summary>The result word the audit line carries for a code that didn't pass (it always starts with "refused").</summary>
    public static string AuditResult(OwnerCodeResult result) => result switch
    {
        OwnerCodeResult.NotSetUp => "refused: two-factor isn't on",
        OwnerCodeResult.Unreadable => "refused: two-factor can't be checked",
        OwnerCodeResult.Locked => "refused: locked",
        OwnerCodeResult.SaveFailed => "refused: code couldn't be marked used",
        _ => "refused: two-factor code",
    };

    private static Guid? Principal(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(StaffSignIn.PrincipalClaim), out var id) ? id : null;
}
