using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Pages.Profile;
using GridGate.Launcher.Security;
using GridGate.Launcher.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace GridGate.Launcher.Pages.Setup;

/// <summary>
/// The setup step "Turn on two-factor" (Region Server and Connected plans, right after "Your avatar"). The owner's account lives on
/// another grid, so two-factor kept on this computer is set up while the proof the avatar step handed out
/// (<see cref="OwnerEnrolProof.FromSetup"/>) still says the setup link got this person here. The same steps, store, sealed form
/// and wording as the staff two-factor page. Without the proof (after the hour, in another browser) the page says how to get a
/// one-time link instead. Already on: the step is done. Only the owner may open it.
/// </summary>
public sealed class SetupTwoFactorModel(TwoFactorStore store, Func<LauncherSettings> settings, IGridController grid, AuditLog audit,
    IDataProtectionProvider dp, LoginThrottle throttle, OwnerEnrolProof proof, ILogger<SetupTwoFactorModel> log)
    : TwoFactorSetupPageModel(settings, audit, dp, throttle, log)
{
    /// <summary>The owner of a plan this step is for, two-factor off, with no proof: no secret is made.</summary>
    public bool NeedsLink { get; private set; }

    protected override TwoFactorStore Store => store;
    protected override string ShownPurpose => TwoFactorModel.SetupPurpose;
    protected override SignInDoor Door => SignInDoor.Staff;
    protected override string AuditRole => StaffRole.Owner.AuditName();
    protected override string LogArea => "Setup two-factor";
    protected override TwoFactorSetupTexts Texts => TwoFactorModel.SetupTexts;

    public IActionResult OnGet()
    {
        if (Open() is not { } principal) return Redirect("/setup");
        if (State == TwoFactorState.On) return Done();
        if (State == TwoFactorState.Off && !NeedsLink) StartEnrol(principal);
        return Page();
    }

    public async Task<IActionResult> OnPostTurnOnAsync()
    {
        if (Open() is not { } principal) return Redirect("/setup");
        if (State == TwoFactorState.On) return Done();
        if (State != TwoFactorState.Off || NeedsLink) return Page();
        return await TurnOnAsync(principal);
    }

    protected override Task TurnedOnAsync(Guid principal)
    {
        MarkDone();
        OwnerEnrolProof.Clear(Response);
        return Task.CompletedTask;
    }

    /// <summary>The owner's principal when this step is open to them (reading their two-factor state), otherwise null.</summary>
    private Guid? Open()
    {
        var s = Settings();
        if (!SetupSteps.CanOpen(s, SetupSteps.TwoFactorPath) || StaffSignIn.Read(User) is not { } claims
            || !OwnerEnrolRule.Applies(s, claims.Role))
            return null;
        State = store.StateOf(claims.Principal);
        NeedsLink = State == TwoFactorState.Off && !proof.Has(Request, claims.Principal);
        return claims.Principal;
    }

    private IActionResult Done()
    {
        MarkDone();
        return Redirect("/setup");
    }

    private void MarkDone() =>
        grid.UpdateSettings(s => s with { Setup = s.Setup with { OwnerTwoFactorSet = true } }, writeConfig: false);
}
