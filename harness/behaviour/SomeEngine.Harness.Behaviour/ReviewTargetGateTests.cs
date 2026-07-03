using System;
using System.IO;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Behaviour;

public sealed class ReviewTargetGateTests
{
    [Fact]
    public void ActiveAgentRunReviewTargetsAreSatisfied()
    {
        var runId = Environment.GetEnvironmentVariable("SOMEENGINE_AGENT_RUN_ID");
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        var evaluation = ReviewTargetGate.Evaluate(HarnessConfig.ResolveRepoRoot(), runId);
        Assert.True(evaluation.Status == ReviewTargetGateStatus.Pass, evaluation.ToString());
    }

    [Fact]
    public void MissingReviewResultNeedsFix()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "wiki-maintained-for-agent-flow");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.NeedsFix, evaluation.Status);
            Assert.Contains("has no result file", evaluation.ToString());
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void MissingReviewTargetsDirectoryNeedsFixWhenRunIsActive()
    {
        var root = CreateTempRepo();
        try
        {
            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.NeedsFix, evaluation.Status);
            Assert.Contains("no review-targets directory", evaluation.ToString());
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void EmptyReviewTargetsDirectoryNeedsFix()
    {
        var root = CreateTempRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".agent-runs", "0001", "batch", "review-targets"));

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.NeedsFix, evaluation.Status);
            Assert.Contains("no review target files", evaluation.ToString());
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void ResultWithoutMatchingTargetBreaksHarness()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "runtime-outside-first-round-boundary");
            WriteResult(root, "runtime-outside-first-round-boundary", "{ \"pass\": true, \"comment\": \"\" }");
            WriteResult(root, "stale-result", "{ \"pass\": true, \"comment\": \"\" }");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.HarnessBroken, evaluation.Status);
            Assert.Contains("no matching review target", evaluation.ToString());
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void PassingReviewResultPassesGate()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "harness-change-does-not-weaken-contract");
            WriteResult(root, "harness-change-does-not-weaken-contract", "{ \"pass\": true, \"comment\": \"\" }");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.True(evaluation.Status == ReviewTargetGateStatus.Pass, evaluation.ToString());
            Assert.Equal(1, evaluation.TargetCount);
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void NamingResearchTargetRequiresPassingComment()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "new-first-round-names-are-researched");
            WriteResult(root, "new-first-round-names-are-researched", "{ \"pass\": true, \"comment\": \"\" }");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.HarnessBroken, evaluation.Status);
            Assert.Contains("required comment", evaluation.ToString());
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void NamingResearchTargetPassesWithComment()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "new-first-round-names-are-researched");
            WriteResult(root, "new-first-round-names-are-researched", "{ \"pass\": true, \"comment\": \"No new first-round names were introduced.\" }");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.Pass, evaluation.Status);
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void FailedReviewResultNeedsFix()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "migration-has-no-temporary-exceptions");
            WriteResult(root, "migration-has-no-temporary-exceptions", "{ \"pass\": false, \"comment\": \"Temporary allowlist was introduced.\" }");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.NeedsFix, evaluation.Status);
            Assert.Contains("Temporary allowlist", evaluation.ToString());
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void FailedReviewResultWithoutCommentBreaksHarness()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "render-domain-remains-backend-free");
            WriteResult(root, "render-domain-remains-backend-free", "{ \"pass\": false, \"comment\": \"\" }");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.HarnessBroken, evaluation.Status);
            Assert.Contains("failed without a comment", evaluation.ToString());
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void NeedsGrillCommentReturnsNeedsGrill()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "render-boundary-not-semantically-bypassed");
            WriteResult(root, "render-boundary-not-semantically-bypassed", "{ \"pass\": false, \"comment\": \"NEEDS_GRILL: Render boundary contradicts requested migration.\" }");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.NeedsGrill, evaluation.Status);
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void InvalidResultJsonBreaksHarness()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "review-targets-are-specific-not-categories");
            WriteResult(root, "review-targets-are-specific-not-categories", "not json");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.HarnessBroken, evaluation.Status);
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void HarnessBrokenEvaluationPrintsRunnerStatusToken()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "review-result-status-token");
            WriteResult(root, "review-result-status-token", "not json");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.StartsWith("HARNESS_BROKEN:", evaluation.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    [Fact]
    public void MissingPassFieldBreaksHarness()
    {
        var root = CreateTempRepo();
        try
        {
            WriteTarget(root, "legacy-reference-not-promoted-to-product");
            WriteResult(root, "legacy-reference-not-promoted-to-product", "{ \"comment\": \"missing pass\" }");

            var evaluation = ReviewTargetGate.Evaluate(root, "0001");

            Assert.Equal(ReviewTargetGateStatus.HarnessBroken, evaluation.Status);
        }
        finally
        {
            DeleteTempRepo(root);
        }
    }

    private static string CreateTempRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "SomeEngineReviewTargetGateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteTarget(string root, string targetId)
    {
        var targetsDir = Path.Combine(root, ".agent-runs", "0001", "batch", "review-targets");
        Directory.CreateDirectory(targetsDir);
        File.WriteAllText(Path.Combine(targetsDir, targetId + ".md"), "# " + targetId);
    }

    private static void WriteResult(string root, string targetId, string json)
    {
        var resultsDir = Path.Combine(root, ".agent-runs", "0001", "batch", "review-results");
        Directory.CreateDirectory(resultsDir);
        File.WriteAllText(Path.Combine(resultsDir, targetId + ".json"), json);
    }

    private static void DeleteTempRepo(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

