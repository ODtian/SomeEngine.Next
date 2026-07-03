using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Behaviour;

/// <summary>
/// Cross-validates the last commit message intent against the changed code shape.
/// Thresholds come from harness/config.json; the executable checks own the rules.
/// </summary>
public sealed class DiffIntentGateTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void LastCommitHasIntentLabel()
    {
        if (HasPendingWorktreeDiff())
        {
            return;
        }

        string log = Repo.Git("log --oneline -1 --no-decorate");
        if (string.IsNullOrWhiteSpace(log))
        {
            return;
        }

        string msg = Repo.Git("log -1 --format=%s%n%b");
        Assert.True(HasIntentLabel(msg),
            $"Commit message lacks an intent label. Expected one of: [{string.Join("/", Config.DiffIntent.Labels)}].\nMessage:\n{msg}");
    }

    [Fact]
    public void RefactorCommitDoesNotStackNewFunctions()
    {
        string log = Repo.Git("log --oneline -1 --no-decorate");
        if (string.IsNullOrWhiteSpace(log))
        {
            return;
        }

        string msg = Repo.Git("log -1 --format=%s%n%b");
        if (HasPendingWorktreeDiff() || !TryGetIntent(msg, out string intent) || intent != "refactor")
        {
            return;
        }

        int added = CountFunctionAdditions();
        int removed = CountFunctionRemovals();
        int tolerance = Config.DiffIntent.RefactorNewFunctionTolerance;
        Assert.True(added <= removed + tolerance,
            $"Refactor commit added {added} functions, removed {removed} (tolerance {tolerance}). " +
            "Refactor should move/extract, not stack new functions. " +
            "If new behavior is needed, declare [feature].");
    }

    [Fact]
    public void BugfixCommitTouchesFewFiles()
    {
        string log = Repo.Git("log --oneline -1 --no-decorate");
        if (string.IsNullOrWhiteSpace(log))
        {
            return;
        }

        string msg = Repo.Git("log -1 --format=%s%n%b");
        if (HasPendingWorktreeDiff() || !TryGetIntent(msg, out string intent) || intent != "bugfix")
        {
            return;
        }

        string[] files = Repo.Git("diff HEAD~1 --name-only")
            .Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        int max = Config.DiffIntent.BugfixMaxFiles;
        Assert.True(files.Length <= max,
            $"Bugfix commit touched {files.Length} files (max {max} from config). Bugfix must be surgical. " +
            "If the scope is larger, declare [refactor] or [feature].");
    }

    private static bool HasPendingWorktreeDiff()
    {
        string status = Repo.Git("status --porcelain");
        return !string.IsNullOrWhiteSpace(status);
    }

    private bool HasIntentLabel(string msg) =>
        Config.DiffIntent.Labels.Any(l => msg.Contains($"[{l}]", StringComparison.OrdinalIgnoreCase));

    private bool TryGetIntent(string msg, out string intent)
    {
        foreach (var l in Config.DiffIntent.Labels)
        {
            if (msg.Contains($"[{l}]", StringComparison.OrdinalIgnoreCase))
            {
                intent = l;
                return true;
            }
        }

        intent = "";
        return false;
    }

    private static int CountFunctionAdditions()
    {
        string diff = Repo.Git("diff HEAD~1 --unified=0 -- \"*.cs\"");
        return diff.Split('\n')
            .Count(l => l.StartsWith('+')
                && !l.StartsWith("+++")
                && (l.Contains("public ") || l.Contains("private ") || l.Contains("internal ") || l.Contains("protected "))
                && l.Contains('(')
                && (l.Contains('{') || l.Contains("=>")));
    }

    private static int CountFunctionRemovals()
    {
        string diff = Repo.Git("diff HEAD~1 --unified=0 -- \"*.cs\"");
        return diff.Split('\n')
            .Count(l => l.StartsWith('-')
                && !l.StartsWith("---")
                && (l.Contains("public ") || l.Contains("private ") || l.Contains("internal ") || l.Contains("protected "))
                && l.Contains('(')
                && (l.Contains('{') || l.Contains("=>")));
    }
}