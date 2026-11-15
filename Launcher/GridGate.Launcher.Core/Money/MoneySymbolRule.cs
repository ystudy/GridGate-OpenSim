namespace GridGate.Launcher.Core.Money;

/// <summary>
/// What a currency symbol may be (money plan 2, task 9; re-checked when INIs are written, money plan 3): 1 to 4
/// characters, no spaces or control characters, and nothing that would break the INI line Robust reads it from
/// (<c>[LoginService] Currency</c>) or the viewer's text.
/// </summary>
public static class MoneySymbolRule
{
    public const string Fallback = "C$";

    // A lone '$' is fine (C$, L$). '{' and '}' would let OpenSim's INI reader expand "${...}" (money review 2026-11-15,
    // H-3: "${x}" stops Robust starting), and '#' starts a comment.
    private static readonly char[] Forbidden = [';', '#', '"', '=', '[', ']', '{', '}', '<', '>', '&', '\\'];

    public static bool Valid(string? symbol) =>
        symbol is { Length: >= 1 and <= 4 } && !symbol.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || Forbidden.Contains(c));
}
