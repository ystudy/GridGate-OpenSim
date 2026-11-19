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
    public void A_paired_server_hides_its_key_hash_and_machine_value()
    {
        var server = new PairedServer(Guid.NewGuid(), "192.168.1.20", Secret, DateTimeOffset.UnixEpoch, 9000, Secret + "-machine");

        NeverPrints(server);
        Assert.DoesNotContain(Secret + "-machine", server.ToString());
        Assert.Contains("192.168.1.20", server.ToString());
    }

    [Fact]
    public void A_pairing_request_hides_the_machine_id_it_carries()
    {
        // The request type is internal to the launcher: built the way the JSON body would be.
        var type = typeof(Program).Assembly.GetType("GridGate.Launcher.Money.MoneyApi+PairBody")!;
        var body = Activator.CreateInstance(type, "192.168.1.20", 9000, Secret, Secret + "-nonce", Secret + "-proof")!;

        NeverPrints(body);
        Assert.Contains("192.168.1.20", body.ToString());
    }

    [Fact]
    public void The_machine_value_a_region_server_sends_is_a_hash_that_gives_the_machine_id_away_to_nobody()
    {
        var sent = PairedServer.MachineKeyOf("7a1e5c3b-0000-4000-8000-000000000001");

        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("gridgate-money-machine:7a1e5c3b-0000-4000-8000-000000000001"))).ToLowerInvariant(), sent);
        Assert.True(PairedServer.IsMachineKey(sent));
        Assert.DoesNotContain("7a1e5c3b", sent);
        Assert.False(PairedServer.IsMachineKey("7a1e5c3b-0000-4000-8000-000000000001"));
        Assert.False(PairedServer.IsMachineKey(sent.ToUpperInvariant()));
        Assert.False(PairedServer.IsMachineKey(null));
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

    /// <summary>No log line of the Gloebit module, at any level, prints an app key, a secret, a callback's members or a whole response.</summary>
    [Fact]
    public void No_gloebit_module_log_line_prints_a_key_a_secret_or_a_whole_response()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "addon-modules"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var statement = new System.Text.RegularExpressions.Regex(@"\bm_log\.\w+\s*\(.*?\);", System.Text.RegularExpressions.RegexOptions.Singleline);
        var seen = 0;
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir.FullName, "addon-modules", "Gloebit", "GloebitMoneyModule"), "*.cs", SearchOption.AllDirectories))
        {
            // Commented-out lines are not code.
            var code = string.Join(Environment.NewLine, File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (System.Text.RegularExpressions.Match m in statement.Matches(code))
            {
                seen++;
                // What may appear: the masked key, a dictionary's key names, and a named field that isn't a key, secret, token or code.
                var rest = System.Text.RegularExpressions.Regex.Replace(m.Value, @"MaskedKey\([^)]*\)|\b\w+\.Keys\b|m_secret == null|\w+\[""(?![^""]*(key|secret|token|code))[^""]*""\]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // The words inside the message's own text are not values.
                rest = System.Text.RegularExpressions.Regex.Replace(rest, @"""(?:[^""\\]|\\.)*""", "");
                if (System.Text.RegularExpressions.Regex.IsMatch(rest,@"appKey|\.AppKey|m_key\b|m_secret|requestData|responseData|\be\.Value", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    bad.Add($"{Path.GetFileName(file)}: {string.Join(' ', m.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}");
            }
        }
        Assert.True(seen > 50, $"the scan found {seen} log lines");
        Assert.Empty(bad);
    }
}
