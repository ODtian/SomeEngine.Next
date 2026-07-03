using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SomeEngine.Harness.Behaviour;

public sealed class AcceptedRunLanguageTests
{
    private static readonly string[] RejectedTerms =
    [
        "debt",
        "债务",
        "todo",
        "主清单",
        "可执行证据",
    ];

    [Fact]
    public void RunArtifactsAndDurableDocsUseAcceptedClassificationLanguage()
    {
        string root = SomeEngine.Harness.Core.HarnessConfig.ResolveRepoRoot();
        var artifacts = new List<ScannedArtifact>();

        string? runId = Environment.GetEnvironmentVariable("SOMEENGINE_AGENT_RUN_ID");
        if (!string.IsNullOrWhiteSpace(runId))
        {
            AddFileIfExists(artifacts, Path.Combine(root, ".agent-runs", runId, "batch", "instructions.md"));
            AddFileIfExists(artifacts, Path.Combine(root, ".agent-runs", runId, "batch", "report.md"));
            AddFileIfExists(artifacts, Path.Combine(root, ".agent-runs", runId, "batch", "status.json"));
            AddMarkdownFiles(artifacts, Path.Combine(root, ".agent-runs", runId, "batch", "review-targets"));
            AddFiles(artifacts, Path.Combine(root, ".agent-runs", runId, "batch", "review-results"), "*.json");
        }

        AddMarkdownFiles(artifacts, Path.Combine(root, "docs"));
        AddDirectoryNames(artifacts, Path.Combine(root, "docs"));
        AddFileIfExists(artifacts, Path.Combine(root, "wiki", "architecture", "Product-Boundary.md"));
        AddFileIfExists(artifacts, Path.Combine(root, "wiki", "architecture", "Render-Boundaries.md"));
        AddFileIfExists(artifacts, Path.Combine(root, "wiki", "harness", "Harness-Definition.md"));

        var failures = new List<string>();
        foreach (ScannedArtifact artifact in artifacts.DistinctBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase))
        {
            string path = artifact.Path;
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            string pathText = relative.Replace('/', ' ');
            string content = artifact.ReadContent ? File.ReadAllText(path) : string.Empty;

            foreach (string rejected in RejectedTerms)
            {
                if (pathText.Contains(rejected, StringComparison.OrdinalIgnoreCase)
                    || content.Contains(rejected, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{relative} uses rejected run/process wording '{rejected}'. Use 本轮已完成 / 本轮未完成 / 不属于本轮.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Run artifacts or durable docs use wording outside the accepted first-round model:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void HarnessDoesNotUseLegacyTestMigrationInventoryAsFirstRoundCompletionGate()
    {
        string root = SomeEngine.Harness.Core.HarnessConfig.ResolveRepoRoot();
        string inventoryFixture = Path.Combine(root, "harness", "fixtures", "legacy-test-migration-inventory.json");
        string inventoryTest = Path.Combine(root, "harness", "architecture", "SomeEngine.Harness.Architecture", "LegacyMigrationInventoryTests.cs");

        Assert.False(File.Exists(inventoryFixture), "First-round completion must not depend on a generated legacy test migration inventory fixture.");
        Assert.False(File.Exists(inventoryTest), "First-round completion must not depend on a legacy test migration inventory harness check.");
    }

    private static void AddFileIfExists(List<ScannedArtifact> artifacts, string path)
    {
        if (File.Exists(path))
        {
            artifacts.Add(new ScannedArtifact(path, ReadContent: true));
        }
    }

    private static void AddMarkdownFiles(List<ScannedArtifact> artifacts, string directory)
        => AddFiles(artifacts, directory, "*.md");

    private static void AddFiles(List<ScannedArtifact> artifacts, string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        artifacts.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(path => new ScannedArtifact(path, ReadContent: true)));
    }

    private static void AddDirectoryNames(List<ScannedArtifact> artifacts, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        artifacts.AddRange(Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
            .Select(path => new ScannedArtifact(path, ReadContent: false)));
    }

    private readonly record struct ScannedArtifact(string Path, bool ReadContent);
}
