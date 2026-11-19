using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Setup;

/// <summary>
/// The read, show and confirm steps of joining a Grid Asset Server, shared by the setup join step and Advanced settings.
/// The first post reads the join information and shows it (Pages/Shared/_JoinFound.cshtml); the confirm post reads it
/// again, never trusting posted values, and saves it with <see cref="Apply"/>.
/// </summary>
public static class JoinForm
{
    /// <summary>
    /// The join information at the typed <paramref name="address"/>, or null with the owner's message: a mistyped
    /// address, or the <see cref="GridJoinException"/> the read gave.
    /// </summary>
    public static async Task<(JoinedGrid? Found, string? Error)> ReadAsync(IGridJoinClient client, string? address, CancellationToken ct)
    {
        if (JoinAddress.Parse(address, out var error) is not { } at) return (null, error);
        try
        {
            return (await client.ReadAsync(at.Host, at.Port, ct), null);
        }
        catch (GridJoinException e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>The address as the owner would type it: the host, with the port only when it isn't the usual one.</summary>
    public static string AddressOf(JoinedGrid joined) =>
        joined.PublicPort == JoinAddress.DefaultPort ? joined.Host : $"{joined.Host}:{joined.PublicPort}";

    /// <summary>A warning naming both versions when the Grid Asset Server runs another GridGate version; null when they match.</summary>
    public static string? VersionWarning(JoinedGrid found) =>
        found.Version == LauncherVersion.Current
            ? null
            : $"The Grid Asset Server runs GridGate {found.Version}, and this computer runs GridGate {LauncherVersion.Current}. "
              + "Both computers should run the same version. You can still join, but update the older one when you can.";

    /// <summary>
    /// The settings after joining <paramref name="read"/>: the join itself, and the grid's name as this computer's. A money
    /// pairing with another Grid Asset Server is dropped: its key works only there (money plan 3). A Gloebit app is saved
    /// for one grid: it is dropped when the host differs from the one joined before (with no earlier join there is no grid to differ from).
    /// </summary>
    public static LauncherSettings Apply(LauncherSettings s, JoinedGrid read)
    {
        var money = s.Money.Pairing is { } p && !string.Equals(p.GridHost, read.Host, StringComparison.OrdinalIgnoreCase) ? s.Money with { Pairing = null } : s.Money;
        if (s.Join is { } old && !string.Equals(old.Host, read.Host, StringComparison.OrdinalIgnoreCase)) money = money with { Gloebit = null };
        return s with { Join = read, GridName = read.GridName, Money = money };
    }
}

/// <summary>What Pages/Shared/_JoinFound.cshtml shows: the join information read, and the address to read again on confirm.</summary>
/// <param name="Found">The join information the first read gave.</param>
/// <param name="Address">The address, as the owner would type it, posted back with "Join this grid" or "Change the address".</param>
/// <param name="Repoint">True on Advanced settings, where a finished Region Server moves its regions to another grid.</param>
/// <param name="AskPassword">True when the confirm form asks for the estate owner's password on the new grid (Advanced, with an owner saved).</param>
public sealed record JoinFoundModel(JoinedGrid Found, string Address, bool Repoint = false, bool AskPassword = false,
    bool AskCode = false, bool NeedsTwoFactor = false);
