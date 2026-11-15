using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// The one place code reads the mode money actually runs in (money plan 2, task 1). It goes through
/// <see cref="MoneyRelease.Effective(MoneySettings)"/>, so it reads Off until the owner has approved the review; tests pass
/// <see cref="Open"/> to see the chosen mode.
/// </summary>
public sealed class MoneyGate(Func<LauncherSettings> settings, Func<MoneySettings, MoneyMode>? effective = null)
{
    /// <summary>For tests only: the chosen mode, as if it were approved.</summary>
    public static readonly Func<MoneySettings, MoneyMode> Open = m => m.Mode;

    private readonly Func<MoneySettings, MoneyMode> _effective = effective ?? MoneyRelease.Effective;

    /// <summary>The mode of the grid this computer belongs to (<see cref="MoneyPlace.GridMode"/>).</summary>
    public MoneyMode Effective() => MoneyPlace.GridMode(settings(), _effective);

    public bool On => Effective() != MoneyMode.Off;

    /// <summary>Whether GridGate's ledger and money service run here (<see cref="MoneyPlace.ServiceHere"/>).</summary>
    public bool ServiceHere => MoneyPlace.ServiceHere(settings(), _effective);

    /// <summary>The mode the ledger, service and their pages run in here: <see cref="Effective"/>, or Off where they don't run.</summary>
    public MoneyMode ServiceMode() => ServiceHere ? Effective() : MoneyMode.Off;

    /// <summary>Whether the release gate lets <paramref name="mode"/> run at all, whatever this computer's plan.</summary>
    public bool Approved(MoneyMode mode) => _effective(new MoneySettings(Mode: mode)) == mode;

    /// <summary>Whether choosing <paramref name="mode"/> would actually run it: what the Money page may offer.</summary>
    public bool Allows(MoneyMode mode) => _effective(settings().Money with { Mode = mode }) == mode;
}
