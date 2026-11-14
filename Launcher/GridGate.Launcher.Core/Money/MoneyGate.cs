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

    public MoneyMode Effective() => _effective(settings().Money);

    public bool On => Effective() != MoneyMode.Off;
}
