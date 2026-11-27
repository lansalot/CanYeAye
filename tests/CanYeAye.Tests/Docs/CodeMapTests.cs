using System.Text.RegularExpressions;

namespace CanYeAye.Tests.Docs;

/// <summary>
/// Keeps <c>docs/CODE-MAP.md</c> honest: every repository path it names in backticks must still
/// exist. Rename a file and forget the map, and this fails instead of an agent being sent to a
/// path that is gone. Symbols are deliberately not checked - the compiler owns those.
/// </summary>
public class CodeMapTests
{
    private static readonly Regex Backticked = new("`([^`\r\n]+)`", RegexOptions.Compiled);

    private static readonly string[] RepoPrefixes = ["src/", "tests/", "docs/", "build/"];

    [Fact]
    public void EveryReferencedPathExists()
    {
        var root = RepoRoot();
        if (root is null)
        {
            return; // not running from a source checkout; nothing to verify against
        }

        var mapPath = Path.Combine(root, "docs", "CODE-MAP.md");
        Assert.True(File.Exists(mapPath), $"docs/CODE-MAP.md is missing (looked in {mapPath}).");

        var missing = new List<string>();
        foreach (Match match in Backticked.Matches(File.ReadAllText(mapPath)))
        {
            var token = match.Groups[1].Value.Trim().Replace('\\', '/').TrimEnd('/');
            if (!LooksLikeRepoPath(token))
            {
                continue;
            }

            if (!Path.Exists(Path.Combine(root, token)))
            {
                missing.Add(token);
            }
        }

        Assert.True(
            missing.Count == 0,
            "docs/CODE-MAP.md references paths that no longer exist:" + Environment.NewLine +
            string.Join(Environment.NewLine, missing.Distinct()));
    }

    [Fact]
    public void IsDiscoverableFromAgentInstructions()
    {
        var root = RepoRoot();
        if (root is null)
        {
            return;
        }

        var agents = Path.Combine(root, "AGENTS.md");
        Assert.True(File.Exists(agents), $"AGENTS.md is missing (looked in {agents}).");
        Assert.Contains("docs/CODE-MAP.md", File.ReadAllText(agents));
    }

    private static bool LooksLikeRepoPath(string token) =>
        RepoPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)) &&
        !token.Contains('<') &&
        !token.Contains(' ');

    /// <summary>Walks up from the test output directory to the directory holding the solution.</summary>
    private static string? RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CanYeAye.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
