using System.Text.RegularExpressions;

namespace TicketingPlatform.UnitTests.Architecture;

/// <summary>
/// The rule that makes the access scopes mean something: IgnoreQueryFilters may appear in exactly
/// ONE file. Without this the scopes are a naming convention, and conventions lose to deadlines -
/// the next repository method under time pressure reaches for the escape hatch and re-introduces
/// exactly the "isolation depends on the predicate the author remembered" problem the scopes exist
/// to remove.
///
/// A source scan rather than a Roslyn analyzer on purpose: one rule, no new project, fails in CI
/// with a message that says what to do. Escalate to an analyzer only if this gets routinely
/// bypassed - a build error is stronger, but it is a lot of machinery for a single rule.
/// </summary>
public class AccessScopeEnforcementTests
{
    private const string ScopeFile = "AccessScopes.cs";

    /// <summary>Matches real calls, not the word inside a // or /// comment.</summary>
    private static readonly Regex Call = new(@"^\s*(?!//)(?:.*\.)?IgnoreQueryFilters\s*\(", RegexOptions.Compiled);

    private static string SourceRoot()
    {
        // Walk up from the test binary to the repository root, then into src/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        Assert.NotNull(dir); // the repo layout changed if this fails
        return Path.Combine(dir!.FullName, "src");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void IgnoreQueryFilters_IsCalledOnlyFromTheAccessScopes()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (Path.GetFileName(file) == ScopeFile)
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Call.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "IgnoreQueryFilters() escaped the access scopes. The tenant filter is the platform's " +
            "default-deny; bypassing it outside a scope means isolation depends on whatever " +
            $"predicate this call site remembers to write. Use TenantScope / CustomerScope / " +
            $"PublicScope / PlatformScope / SystemScope in {ScopeFile} instead.{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheAccessScopesFileExistsAndOwnsTheBypasses()
    {
        // Guards the other direction: if someone deletes or renames the scope file, the rule above
        // would pass vacuously while every bypass had silently moved somewhere else.
        var scopeFile = SourceFiles().SingleOrDefault(f => Path.GetFileName(f) == ScopeFile);

        Assert.NotNull(scopeFile);
        Assert.Contains("IgnoreQueryFilters", File.ReadAllText(scopeFile!));
    }
}
