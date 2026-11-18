using GridGate.Launcher.Core.Email;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Iar;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Pages.Security;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Records that carry a secret never print it: not in <c>ToString</c>, not through a logger's <c>{Value}</c>.</summary>
public sealed class SecretsInRecordsTests
{
    private const string Secret = "s3cr3t-value-9f2a";

    private static void NeverPrints(object value)
    {
        Assert.DoesNotContain(Secret, value.ToString());
        Assert.DoesNotContain(Secret, $"{value}");
    }

    [Fact]
    public void A_server_pairing_hides_its_key()
    {
        var pairing = new ServerPairing("grid.example.org", "AB:CD", Secret, DateTimeOffset.UnixEpoch);

        NeverPrints(pairing);
        Assert.Contains("grid.example.org", pairing.ToString());
    }

    [Fact]
    public void Money_settings_hide_program_keys_and_the_pairing_key()
    {
        var m = new MoneySettings(
            ProgramKeys: new() { ["main"] = Secret },
            Pairing: new ServerPairing("grid.example.org", "AB:CD", Secret, DateTimeOffset.UnixEpoch),
            Gloebit: new GloebitSettings("sandbox", "0b9e3c2a-1111-4c5d-8e9f-0123456789ab", Secret, "Owner"));

        NeverPrints(m);
        Assert.DoesNotContain("0b9e3c2a-1111-4c5d-8e9f-0123456789ab", m.ToString());
    }

    [Fact]
    public void Launcher_settings_hide_every_secret()
    {
        var s = new LauncherSettings
        {
            Db = new DatabaseSecrets(Secret, Secret, Secret, Secret),
            Console = new ConsoleSecrets("user", Secret),
            MachineId = "m",
            SetupToken = Secret,
            Money = new MoneySettings(ProgramKeys: new() { ["main"] = Secret }),
        };

        NeverPrints(s);
        NeverPrints(s.Db);
        NeverPrints(s.Console);
    }

    [Fact]
    public void Mail_account_hides_its_protected_password()
    {
        var a = new EmailAccount(Guid.NewGuid(), "x", "smtp.example.org", 587, default, "me", Secret, "n", "a@example.org", null, null, null, false, 1, null, null);

        NeverPrints(a);
    }

    [Fact]
    public void Sign_up_and_model_logins_hide_their_passwords()
    {
        NeverPrints(new SignUpForm("A", "B", "a@example.org", Secret, Secret, true));
        NeverPrints(new ModelLogin("Avatar", "Model", Secret, "Folder"));
    }

    [Fact]
    public void Download_links_and_challenges_hide_their_tokens()
    {
        NeverPrints(new IarReadyFile(Guid.NewGuid(), "f.iar", Secret, "stamp", DateTimeOffset.UnixEpoch, [], 1, 1));
        NeverPrints(new CaptchaChallenge("<svg/>", "q", Secret, 1, 2));
    }

    [Fact]
    public void The_gloebit_app_still_hides_its_secret() =>
        NeverPrints(new GloebitSettings("sandbox", "0b9e3c2a-1111-4c5d-8e9f-0123456789ab", Secret, "Owner"));

    [Fact]
    public void The_gloebit_module_logs_its_app_key_masked()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "addon-modules"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir.FullName, "addon-modules", "Gloebit", "GloebitMoneyModule", "GloebitMoneyModule.cs"));
        var at = source.IndexOf("Initialised. Gloebit enabled", StringComparison.Ordinal);
        Assert.True(at > 0);
        var statement = source[at..source.IndexOf(");", at, StringComparison.Ordinal)];

        Assert.DoesNotContain(", m_key,", statement);
        Assert.Contains("MaskedKey(m_key)", statement);
    }
}
