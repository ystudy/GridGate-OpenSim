namespace GridGate.Launcher.Core.Money;

/// <summary>
/// The owner's safety gate (money design §12). The owner approved the money code's security checks on 2026-11-19, which
/// turned on <see cref="PlayCoinsApproved"/> (Play coins and Gloebit). Real coins stay Off until a human security reviewer
/// has also checked the code and the owner approves that: only then does <see cref="RealCoinsApproved"/> turn on. A
/// hand-edited settings file can't get around it.
/// </summary>
public static class MoneyRelease
{
    public const bool PlayCoinsApproved = true;
    public const bool RealCoinsApproved = false;

    /// <summary>The mode the grid actually runs in: <see cref="MoneyMode.Off"/> unless the chosen mode is approved.</summary>
    public static MoneyMode Effective(MoneySettings m) => Effective(m, PlayCoinsApproved, RealCoinsApproved);

    /// <summary>The gate with the approvals passed in, so tests can check both sides of it.</summary>
    public static MoneyMode Effective(MoneySettings m, bool playApproved, bool realApproved) => m.Mode switch
    {
        MoneyMode.Play or MoneyMode.Gloebit when playApproved => m.Mode,
        MoneyMode.Real when playApproved && realApproved => MoneyMode.Real,
        _ => MoneyMode.Off,
    };
}
