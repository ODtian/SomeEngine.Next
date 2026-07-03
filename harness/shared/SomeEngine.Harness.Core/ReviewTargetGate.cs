using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SomeEngine.Harness.Core;

public enum ReviewTargetGateStatus
{
    Pass,
    NeedsFix,
    NeedsGrill,
    HarnessBroken,
}

public sealed class ReviewTargetGateEvaluation
{
    public ReviewTargetGateEvaluation(
        ReviewTargetGateStatus status,
        int targetCount,
        IReadOnlyList<string> messages)
    {
        Status = status;
        TargetCount = targetCount;
        Messages = messages;
    }

    public ReviewTargetGateStatus Status { get; }
    public int TargetCount { get; }
    public IReadOnlyList<string> Messages { get; }

    public override string ToString()
        => $"{StatusToken(Status)}: {string.Join(Environment.NewLine, Messages)}";

    private static string StatusToken(ReviewTargetGateStatus status)
        => status switch
        {
            ReviewTargetGateStatus.Pass => "PASS",
            ReviewTargetGateStatus.NeedsFix => "NEEDS_FIX",
            ReviewTargetGateStatus.NeedsGrill => "NEEDS_GRILL",
            ReviewTargetGateStatus.HarnessBroken => "HARNESS_BROKEN",
            _ => status.ToString(),
        };
}

public static class ReviewTargetGate
{
    public static ReviewTargetGateEvaluation Evaluate(string repoRoot, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return new ReviewTargetGateEvaluation(
                ReviewTargetGateStatus.Pass,
                0,
                new[] { "No agent run id supplied; ReviewTargetGate skipped." });
        }

        if (runId.Any(c => !char.IsDigit(c)))
        {
            return new ReviewTargetGateEvaluation(
                ReviewTargetGateStatus.HarnessBroken,
                0,
                new[] { $"Run id '{runId}' is invalid. Run ids must be numeric directory names." });
        }

        var targetsDir = Path.Combine(repoRoot, ".agent-runs", runId, "batch", "review-targets");
        var resultsDir = Path.Combine(repoRoot, ".agent-runs", runId, "batch", "review-results");
        if (!Directory.Exists(targetsDir))
        {
            return new ReviewTargetGateEvaluation(
                ReviewTargetGateStatus.NeedsFix,
                0,
                new[] { $"Run {runId} has no review-targets directory at {targetsDir}." });
        }

        var targetFiles = Directory.GetFiles(targetsDir, "*.md")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        var messages = new List<string>();
        var status = ReviewTargetGateStatus.Pass;
        if (targetFiles.Length == 0)
        {
            PromoteStatus(ref status, ReviewTargetGateStatus.NeedsFix);
            messages.Add($"Run {runId} has no review target files in {targetsDir}.");
        }

        if (Directory.Exists(resultsDir))
        {
            var targetIds = new HashSet<string>(
                targetFiles.Select(Path.GetFileNameWithoutExtension),
                StringComparer.Ordinal);
            foreach (string resultFile in Directory.GetFiles(resultsDir, "*.json")
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                string resultId = Path.GetFileNameWithoutExtension(resultFile);
                if (!targetIds.Contains(resultId))
                {
                    PromoteStatus(ref status, ReviewTargetGateStatus.HarnessBroken);
                    messages.Add($"Review result '{resultId}' has no matching review target file.");
                }
            }
        }

        foreach (var targetFile in targetFiles)
        {
            var targetId = Path.GetFileNameWithoutExtension(targetFile);
            var resultPath = Path.Combine(resultsDir, targetId + ".json");
            if (!File.Exists(resultPath))
            {
                PromoteStatus(ref status, ReviewTargetGateStatus.NeedsFix);
                messages.Add($"Review target '{targetId}' has no result file at {resultPath}.");
                continue;
            }

            EvaluateResultFile(targetId, resultPath, messages, ref status);
        }

        if (messages.Count == 0)
        {
            messages.Add($"All {targetFiles.Length} review target(s) passed for run {runId}.");
        }

        return new ReviewTargetGateEvaluation(status, targetFiles.Length, messages);
    }

    private static void EvaluateResultFile(
        string targetId,
        string resultPath,
        List<string> messages,
        ref ReviewTargetGateStatus status)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(resultPath));
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            PromoteStatus(ref status, ReviewTargetGateStatus.HarnessBroken);
            messages.Add($"Review result for '{targetId}' is not readable JSON: {ex.Message}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                PromoteStatus(ref status, ReviewTargetGateStatus.HarnessBroken);
                messages.Add($"Review result for '{targetId}' must be a JSON object.");
                return;
            }

            if (!root.TryGetProperty("pass", out var passElement) ||
                (passElement.ValueKind != JsonValueKind.True && passElement.ValueKind != JsonValueKind.False))
            {
                PromoteStatus(ref status, ReviewTargetGateStatus.HarnessBroken);
                messages.Add($"Review result for '{targetId}' must contain boolean field 'pass'.");
                return;
            }

            if (!root.TryGetProperty("comment", out var commentElement) || commentElement.ValueKind != JsonValueKind.String)
            {
                PromoteStatus(ref status, ReviewTargetGateStatus.HarnessBroken);
                messages.Add($"Review result for '{targetId}' must contain string field 'comment'.");
                return;
            }

            var passed = passElement.GetBoolean();
            var comment = commentElement.GetString() ?? string.Empty;
            if (passed)
            {
                if (RequiresPassingComment(targetId) && string.IsNullOrWhiteSpace(comment))
                {
                    PromoteStatus(ref status, ReviewTargetGateStatus.HarnessBroken);
                    messages.Add($"Review result for '{targetId}' passed without the required comment.");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(comment))
            {
                PromoteStatus(ref status, ReviewTargetGateStatus.HarnessBroken);
                messages.Add($"Review result for '{targetId}' failed without a comment.");
                return;
            }

            if (comment.StartsWith("NEEDS_GRILL:", StringComparison.Ordinal))
            {
                PromoteStatus(ref status, ReviewTargetGateStatus.NeedsGrill);
                messages.Add($"Review target '{targetId}' requires re-grill: {comment}");
                return;
            }

            PromoteStatus(ref status, ReviewTargetGateStatus.NeedsFix);
            messages.Add($"Review target '{targetId}' failed: {comment}");
        }
    }

    private static void PromoteStatus(ref ReviewTargetGateStatus current, ReviewTargetGateStatus candidate)
    {
        if (Rank(candidate) > Rank(current))
        {
            current = candidate;
        }
    }

    private static int Rank(ReviewTargetGateStatus status)
        => status switch
        {
            ReviewTargetGateStatus.Pass => 0,
            ReviewTargetGateStatus.NeedsFix => 1,
            ReviewTargetGateStatus.NeedsGrill => 2,
            ReviewTargetGateStatus.HarnessBroken => 3,
            _ => 0,
        };

    private static bool RequiresPassingComment(string targetId)
        => string.Equals(targetId, "new-first-round-names-are-researched", StringComparison.Ordinal);
}
