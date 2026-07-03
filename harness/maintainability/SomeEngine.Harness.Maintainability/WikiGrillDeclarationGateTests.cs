using System.Linq;
using System.Text;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Maintainability;

/// <summary>
/// Grill declaration gate for wiki changes. Wiki notes are durable knowledge,
/// so tracked wiki edits must be declared either by commit metadata or by a
/// durable grill transcript that names the edited note/topic. During active
/// uncommitted work this gate checks the current tracked diff against the
/// on-disk grill sessions; when the worktree is clean it falls back to the
/// last committed diff.
/// </summary>
public sealed class WikiGrillDeclarationGateTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();
    private static readonly string WikiPrefix = NormalizePath(Config.Repository.WikiRoot) + "/";
    private const string GrillSessionPrefix = "harness/grill/sessions/";

    [Fact]
    public void WikiEditsAreDeclaredByCommitOrCurrentGrillSession()
    {
        var currentWikiChanges = GitPathLines(Repo.Git($"diff --name-only HEAD -- {Config.Repository.WikiRoot}"))
            .Where(IsWikiPath)
            .ToList();

        if (currentWikiChanges.Count > 0)
        {
            var message = Repo.Git("log -1 --format=%s%n%b");
            var declaredTopics = WikiDeclarationParser.ExtractDeclaredTopics(message);
            var sessionText = ReadAllCurrentGrillSessions();
            var unmatched = currentWikiChanges
                .Where(path => !WikiDeclarationParser.IsCoveredByMessageOrSession(path, message, declaredTopics, sessionText))
                .ToList();

            Assert.True(
                unmatched.Count == 0,
                "Current tracked wiki changes are not covered by a declared grill topic/session: "
                + string.Join(", ", unmatched)
                + ". Add a durable harness/grill/sessions transcript that names each changed wiki note/topic, "
                + "or use [wiki: TopicName] in the commit message.");
            return;
        }

        var log = Repo.Git("log --oneline -1 --no-decorate");
        if (string.IsNullOrWhiteSpace(log)) return; // no commits yet

        var changed = GitPathLines(Repo.Git("diff HEAD~1 --name-only"));
        var wikiChanges = changed.Where(IsWikiPath).ToList();
        if (wikiChanges.Count == 0) return; // no wiki edits in last commit

        var grillSessionChanges = changed
            .Where(path => NormalizePath(path).StartsWith(GrillSessionPrefix, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        var msg = Repo.Git("log -1 --format=%s%n%b");
        var declared = WikiDeclarationParser.ExtractDeclaredTopics(msg);
        var committedSessionText = ReadSessionFiles(grillSessionChanges);

        var missing = wikiChanges
            .Where(path => !WikiDeclarationParser.IsCoveredByMessageOrSession(path, msg, declared, committedSessionText))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Committed wiki changes not covered by a declared grill topic/session: {string.Join(", ", missing)}. "
            + "Use [wiki: TopicName], or commit a harness/grill/sessions transcript that names each wiki note/topic.");
    }

    private static bool IsWikiPath(string path)
        => NormalizePath(path).StartsWith(WikiPrefix, System.StringComparison.OrdinalIgnoreCase);

    private static List<string> GitPathLines(string output)
        => output
            .Split(['\r', '\n'], System.StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .ToList();

    private static string ReadAllCurrentGrillSessions()
    {
        var sessionsRoot = System.IO.Path.Combine(HarnessConfig.ResolveRepoRoot(), "harness", "grill", "sessions");
        if (!System.IO.Directory.Exists(sessionsRoot))
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            System.IO.Directory.GetFiles(sessionsRoot, "*.md", System.IO.SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, System.StringComparer.OrdinalIgnoreCase)
                .Select(System.IO.File.ReadAllText));
    }

    private static string ReadSessionFiles(IReadOnlyCollection<string> relativePaths)
    {
        var root = HarnessConfig.ResolveRepoRoot();
        return string.Join(
            "\n",
            relativePaths
                .Select(path => System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path)))
                .Where(System.IO.File.Exists)
                .OrderBy(path => path, System.StringComparer.OrdinalIgnoreCase)
                .Select(System.IO.File.ReadAllText));
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim();
}

internal static class WikiDeclarationParser
{
    public static List<string> ExtractDeclaredTopics(string msg)
    {
        var topics = new List<string>();
        int i = 0;
        while ((i = msg.IndexOf("[wiki:", i, System.StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = msg.IndexOf(']', i);
            if (end < 0) break;
            var topic = msg[(i + 6)..end].Trim();
            if (topic.Length > 0) topics.Add(topic);
            i = end + 1;
        }
        return topics;
    }

    public static bool IsCoveredByMessageOrSession(
        string wikiPath,
        string message,
        IReadOnlyCollection<string> declaredTopics,
        string sessionText)
    {
        string noteName = System.IO.Path.GetFileNameWithoutExtension(wikiPath.Replace('\\', '/'));
        string normalizedNote = NormalizeTopic(noteName);

        if (declaredTopics.Any(topic => NormalizeTopic(topic).Contains(normalizedNote, System.StringComparison.OrdinalIgnoreCase)
                                        || normalizedNote.Contains(NormalizeTopic(topic), System.StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return NormalizeTopic(message).Contains(normalizedNote, System.StringComparison.OrdinalIgnoreCase)
               || NormalizeTopic(sessionText).Contains(normalizedNote, System.StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTopic(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
