using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Money;

/// <summary>
/// What the money pages show about regions and where people are: which regions are
/// verified, what each key's module last reported, approving a moved region, and where a person is now. A seam so the
/// pages can be tested without a database. A database failure is <see cref="MoneyUnavailableException"/>.
/// </summary>
public interface IMoneyRegions
{
    /// <summary>One state per region Robust lists (<see cref="RegionVerification.AllAsync"/>).</summary>
    Task<IReadOnlyList<RegionState>> AllAsync(LauncherSettings settings, CancellationToken ct);

    /// <summary>Every key's last health call (<see cref="RegionVerification.KeysAsync"/>).</summary>
    Task<IReadOnlyDictionary<string, KeyHeard>> KeysAsync(CancellationToken ct);

    /// <summary>Pins a moved region to the server Robust now lists it at (<see cref="RegionPins.ApproveMoveAsync"/>). The caller audits it.</summary>
    Task ApproveMoveAsync(Guid regionId, Guid toServerId, CancellationToken ct);

    /// <summary>The person's newest location whose session is still logged in (<see cref="MoneyLocations.WhereAgentAsync"/>).</summary>
    Task<SessionLocation?> WhereAgentAsync(Guid agentId, CancellationToken ct);
}

/// <summary>The money service's own records behind <see cref="IMoneyRegions"/>.</summary>
public sealed class MoneyRegions(RegionVerification verification, RegionPins pins, MoneyLocations locations) : IMoneyRegions
{
    public Task<IReadOnlyList<RegionState>> AllAsync(LauncherSettings settings, CancellationToken ct) => verification.AllAsync(settings, ct);

    public Task<IReadOnlyDictionary<string, KeyHeard>> KeysAsync(CancellationToken ct) => verification.KeysAsync(ct);

    public Task ApproveMoveAsync(Guid regionId, Guid toServerId, CancellationToken ct) => pins.ApproveMoveAsync(regionId, toServerId, ct);

    public Task<SessionLocation?> WhereAgentAsync(Guid agentId, CancellationToken ct) => locations.WhereAgentAsync(agentId, ct);
}
