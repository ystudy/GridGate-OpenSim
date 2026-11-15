using System.Text.RegularExpressions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Reads rules out of the served site.css, for tests of layout choices that only CSS can make.</summary>
internal static class SiteCss
{
    public static Task<string> GetAsync(LauncherFactory f, CancellationToken ct) =>
        f.CreateClient().GetStringAsync("/css/site.css", ct);

    /// <summary>The declarations of the rule written for exactly this selector (top level or in the given block), whitespace squeezed.</summary>
    public static string Rule(string css, string selector)
    {
        var m = Regex.Match(css, @"(?:^|[{}/])\s*" + Regex.Escape(selector) + @"\s*\{([^}]*)\}", RegexOptions.Multiline);
        Assert.True(m.Success, $"site.css has no rule for {selector}");
        return Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
    }

    /// <summary>The body of the @media block with exactly this condition.</summary>
    public static string Media(string css, string condition)
    {
        var head = "@media " + condition + " {";
        var start = css.IndexOf(head, StringComparison.Ordinal);
        Assert.True(start >= 0, $"site.css has no {head}");
        var depth = 1;
        for (var i = start + head.Length; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return css[(start + head.Length)..i];
        }
        Assert.Fail($"{head} is not closed");
        return "";
    }
}
