using System.Text.RegularExpressions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// What ships in <c>bin/</c> and the packages (money plan 4, task 6): no upstream Gloebit credentials or contact details, and
/// Gloebit's module only as the package's <c>Gloebit.dll.Proto</c>, built from this tree.
/// </summary>
public sealed partial class ShippedFilesTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("GridGate.sln not found");
    }

    // An active GLBKey or GLBSecret holding a real id; upstream's example keeps all zeros.
    [GeneratedRegex(@"^\s*GLB(Key|Secret)\s*=\s*(?!0{8}-0{4}-0{4}-0{4}-0{12})[0-9a-fA-F]{8}-", RegexOptions.Multiline)]
    private static partial Regex ActiveCredential();

    [Fact]
    public void No_upstream_gloebit_credentials_ship()
    {
        var bin = Path.Combine(Repo(), "bin");
        var texts = Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".ini" or ".example" or ".proto" or ".txt" or ".xml" or ".config")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        var offenders = new List<string>();
        foreach (var file in texts)
        {
            var text = File.ReadAllText(file);
            if (text.Contains("Ferd Frederix", StringComparison.OrdinalIgnoreCase) || text.Contains("fred@outworldz.com", StringComparison.OrdinalIgnoreCase)
                || ActiveCredential().IsMatch(text))
                offenders.Add(Path.GetRelativePath(bin, file));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_stale_gloebit_binary_is_kept_in_the_tree()
    {
        // The package builds its Gloebit.dll.Proto from bin/Gloebit.dll; a committed one would be an old upstream build.
        var ignore = File.ReadAllText(Path.Combine(Repo(), ".gitignore"));
        Assert.Contains("bin/Gloebit.dll.Proto", ignore);
    }

    [Fact]
    public void Package_ships_the_built_gloebit_as_proto()
    {
        var script = File.ReadAllText(Path.Combine(Repo(), "setup", "package.ps1"));

        Assert.Contains("$excludedFiles = @('Gloebit.dll', 'Gloebit.dll.Proto')", script);
        Assert.Matches(@"Copy\(\(Join-Path \$repo 'bin/Gloebit\.dll'\), \(Join-Path \$pkg 'bin/Gloebit\.dll\.Proto'\)", script);
    }
}
