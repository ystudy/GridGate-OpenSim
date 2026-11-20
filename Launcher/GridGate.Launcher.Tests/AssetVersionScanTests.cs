using System.Text.RegularExpressions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Every local script and stylesheet link carries <c>asp-append-version</c>, so its address changes with its contents and
/// a browser loads the new file after an upgrade. Without it, a normal reload could keep running the old script: seen on
/// the Debian 12 VM, where the Regions page map kept an old copy of map-core.js after an upgrade.
/// </summary>
public sealed class AssetVersionScanTests
{
    private static readonly Regex LocalAsset =
        new(@"<(script\b[^>]*\bsrc|link\b[^>]*\bhref)\s*=\s*""/[^""]+""[^>]*>", RegexOptions.IgnoreCase);

    [Fact]
    public void Every_local_script_and_stylesheet_has_a_version()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("GridGate.sln not found");
        var pages = Path.Combine(root, "Launcher", "GridGate.Launcher", "Pages");
        var files = Directory.GetFiles(pages, "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var found = new List<string>();
        var seen = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in LocalAsset.Matches(lines[i]))
                {
                    // Only scripts and stylesheets; other links (icons, the manifest) aren't code a page runs. An /api/
                    // address is made per request and carries its own version (the splash editor's draft.css).
                    if (m.Value.StartsWith("<link", StringComparison.OrdinalIgnoreCase) && !m.Value.Contains("stylesheet")) continue;
                    if (m.Value.Contains("\"/api/")) continue;
                    seen++;
                    if (!m.Value.Contains("asp-append-version=\"true\""))
                        found.Add($"{Path.GetRelativePath(pages, file)}:{i + 1}: {m.Value}");
                }
        }

        Assert.True(seen > 30, $"only {seen} links found: the pattern no longer matches the pages");
        Assert.True(found.Count == 0, "Add asp-append-version=\"true\":\n" + string.Join('\n', found));
    }
}
