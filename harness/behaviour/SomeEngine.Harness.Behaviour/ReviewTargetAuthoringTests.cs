using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SomeEngine.Harness.Behaviour;

public sealed class ReviewTargetAuthoringTests
{
    private static readonly string[] RequiredContractTargets =
    [
        "harness-change-does-not-weaken-contract",
        "migration-has-no-temporary-exceptions",
        "run-classification-uses-accepted-terms",
    ];

    [Fact]
    public void ActiveAgentRunReviewTargetsHaveRequiredSections()
    {
        string? runId = Environment.GetEnvironmentVariable("SOMEENGINE_AGENT_RUN_ID");
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        string root = SomeEngine.Harness.Core.HarnessConfig.ResolveRepoRoot();
        string targetsDir = Path.Combine(root, ".agent-runs", runId, "batch", "review-targets");
        if (!Directory.Exists(targetsDir))
        {
            return;
        }

        var failures = new List<string>();
        foreach (string target in Directory.EnumerateFiles(targetsDir, "*.md").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, target).Replace('\\', '/');
            string markdown = File.ReadAllText(target);
            string targetId = Path.GetFileNameWithoutExtension(target);

            if (!targetId.Contains('-', StringComparison.Ordinal))
            {
                failures.Add($"{relative} target id should be a specific kebab-case objective.");
            }

            RequireSection(markdown, "## What to review", relative, failures);
            RequireSection(markdown, "## Pass conditions", relative, failures);
            RequireSection(markdown, "## Fail conditions", relative, failures);
            RequireSection(markdown, "## NEEDS_GRILL", relative, failures);

            if (!markdown.Contains("NEEDS_GRILL:", StringComparison.Ordinal))
            {
                failures.Add($"{relative} must state when to use NEEDS_GRILL:.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Review targets are not authored as concrete executable-review inputs:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ActiveAgentRunReviewTargetsMatchDeclaredObjectives()
    {
        string? runId = Environment.GetEnvironmentVariable("SOMEENGINE_AGENT_RUN_ID");
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        string root = SomeEngine.Harness.Core.HarnessConfig.ResolveRepoRoot();
        string targetsDir = Path.Combine(root, ".agent-runs", runId, "batch", "review-targets");
        string instructionsPath = Path.Combine(root, ".agent-runs", runId, "batch", "instructions.md");
        Assert.True(Directory.Exists(targetsDir), $"Active run {runId} must have review targets.");
        Assert.True(File.Exists(instructionsPath), $"Active run {runId} must have batch instructions.");

        var actual = Directory.EnumerateFiles(targetsDir, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string instructions = File.ReadAllText(instructionsPath);

        Assert.NotEmpty(actual);
        foreach (string requiredTarget in RequiredContractTargets)
        {
            Assert.Contains(requiredTarget, actual);
        }

        foreach (string targetId in actual)
        {
            Assert.Contains($"`{targetId}`", instructions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ActiveAgentRunBatchInstructionsUseAcceptedAuthoringShape()
    {
        string? runId = Environment.GetEnvironmentVariable("SOMEENGINE_AGENT_RUN_ID");
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        string root = SomeEngine.Harness.Core.HarnessConfig.ResolveRepoRoot();
        string instructionsPath = Path.Combine(root, ".agent-runs", runId, "batch", "instructions.md");
        Assert.True(File.Exists(instructionsPath), $"Active run {runId} must have batch instructions.");

        string markdown = File.ReadAllText(instructionsPath);
        var failures = new List<string>();

        RequireSection(markdown, "## Objective", "instructions.md", failures);
        RequireSection(markdown, "## Inputs", "instructions.md", failures);
        RequireSection(markdown, "## Work Items", "instructions.md", failures);
        RequireSection(markdown, "## Success Criteria", "instructions.md", failures);
        RequireSection(markdown, "## Stop Conditions", "instructions.md", failures);

        foreach (string acceptedTerm in new[] { "本轮已完成", "本轮未完成", "不属于本轮" })
        {
            if (!markdown.Contains(acceptedTerm, StringComparison.Ordinal))
            {
                failures.Add($"instructions.md must use accepted classification term {acceptedTerm}.");
            }
        }

        foreach (string genericRunnerFragment in new[]
        {
            "pwsh -NoProfile",
            "dotnet test",
            "harness-run.json",
            "\"pass\"",
            "\"comment\"",
        })
        {
            if (markdown.Contains(genericRunnerFragment, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"instructions.md must not embed generic runner or review-result schema fragment '{genericRunnerFragment}'.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Batch instructions do not match the accepted Step 2 authoring shape:\n" + string.Join("\n", failures));
    }

    private static void RequireSection(string markdown, string section, string relative, List<string> failures)
    {
        if (!markdown.Contains(section, StringComparison.Ordinal))
        {
            failures.Add($"{relative} missing section {section}.");
        }
    }
}
