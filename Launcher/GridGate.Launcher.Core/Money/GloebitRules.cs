using System.Text.RegularExpressions;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// This computer's Gloebit app (money plan 4): which Gloebit environment, the app's key and secret from Gloebit's merchant
/// tools, and who residents contact about problems. Kept in the owner-only settings file. The secret never prints.
/// </summary>
public sealed record GloebitSettings(string Environment = "sandbox", string Key = "", string Secret = "", string OwnerName = "", string OwnerEmail = "")
{
    /// <summary>Enough to run Gloebit: valid values, with a key, a secret and an owner name.</summary>
    public bool Complete => GloebitRules.Problem(this) is null && Key.Length > 0 && Secret.Length > 0 && OwnerName.Length > 0;

    public override string ToString() =>
        $"GloebitSettings {{ Environment = {Environment}, Key = {GloebitRules.Masked(Key)}, Secret = {(Secret.Length > 0 ? "set" : "none")}, OwnerName = {OwnerName} }}";
}

/// <summary>What a Gloebit app's settings may be (money plan 4).</summary>
public static partial class GloebitRules
{
    public static readonly string[] Environments = ["sandbox", "production"];

    // Characters that would break the INI line the value is written on, or the section around it; and '$', which OpenSim
    // expands as ${Section|Key} (another setting's value, a password even).
    private static readonly char[] IniBreakers = ['"', ';', '[', ']', '$'];

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailShape();

    /// <summary>The first problem, in words for the page, or null.</summary>
    public static string? Problem(GloebitSettings g)
    {
        if (!Environments.Contains(g.Environment)) return "Pick Gloebit's sandbox or production.";
        if (!Guid.TryParseExact(g.Key, "D", out _)) return "The app key is the GUID Gloebit's merchant tools show, like 0b9e3c2a-….";
        if (!Guid.TryParseExact(g.Secret, "D", out _)) return "The app secret is the GUID Gloebit's merchant tools show.";
        if (g.OwnerName.Trim().Length is < 1 or > 64 || Unsafe(g.OwnerName)) return "Give a contact name of 1 to 64 characters, without \" ; [ ] $.";
        if (g.OwnerEmail.Length > 0 && (g.OwnerEmail.Length > 128 || Unsafe(g.OwnerEmail) || !EmailShape().IsMatch(g.OwnerEmail)))
            return "The contact email doesn't look like an address.";
        return null;
    }

    /// <summary>A key shown on a page: its last four characters only.</summary>
    public static string Masked(string key) => key.Length == 0 ? "" : "••••" + key[^Math.Min(4, key.Length)..];

    private static bool Unsafe(string s) => s.Any(c => char.IsControl(c) || IniBreakers.Contains(c));
}
