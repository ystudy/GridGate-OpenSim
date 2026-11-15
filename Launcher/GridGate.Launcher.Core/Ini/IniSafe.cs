namespace GridGate.Launcher.Core.Ini;

/// <summary>
/// What may be written as an INI value that OpenSim reads. OpenSim reads its INIs with Nini, which treats <c>;</c> and
/// <c>#</c> as comments, <c>"</c> as quoting and <c>[ ]</c> as sections, and expands <c>${Section|Key}</c> into another
/// setting's value in every value, a password or the money key included. A value another computer sent (grid information)
/// must pass this before it is saved and again before it is written, so it can't copy a secret into an address every
/// viewer is given (money review 2026-11-15, H-1).
/// </summary>
public static class IniSafe
{
    /// <summary>Characters Nini reads as more than text: <c>" ; # $ [ ] { } = \</c>.</summary>
    public const string Forbidden = "\";#$[]{}=\\";

    /// <summary>True when <paramref name="value"/> has no control characters and none of <see cref="Forbidden"/>.</summary>
    public static bool IsText(string value) => !value.Any(c => char.IsControl(c) || Forbidden.Contains(c));

    /// <summary>
    /// <paramref name="value"/> when it is an absolute http(s) address with a host, no white space and nothing Nini would
    /// read as more than text; otherwise null.
    /// </summary>
    public static string? Address(string? value) =>
        value is { Length: > 0 } v && !v.Any(char.IsWhiteSpace) && IsText(v)
        && Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? v
            : null;

    /// <summary>
    /// <paramref name="value"/>, trimmed, when it is 1 to 64 characters with nothing Nini would read as more than text;
    /// otherwise null. The same rule as a grid name the owner types (<c>SettingsValidator.GridNameError</c>).
    /// </summary>
    public static string? Name(string? value) =>
        value?.Trim() is { Length: >= 1 and <= 64 } t && IsText(t) ? t : null;
}
