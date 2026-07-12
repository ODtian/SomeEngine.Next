using System;
using System.Linq;
using System.IO;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class HarnessScriptBoundaryTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void ProductAndCoverageScriptsUseDeclaredTestProjectCatalog()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var productScript = File.ReadAllText(Path.Combine(root, "harness", "RunProductTests.ps1"));
        var coverageScript = File.ReadAllText(Path.Combine(root, "harness", "coverage", "GenerateCoverage.ps1"));

        Assert.Contains("$config.projects.testProjects", productScript);
        Assert.Contains("$config.productTests.warningTraits", productScript);
        Assert.Contains("$config.projects.testProjects", coverageScript);
        Assert.DoesNotContain("Get-ChildItem -Path $testsRoot", productScript);
        Assert.DoesNotContain("Join-Path $repoRoot \"tests\"", coverageScript);
    }

    [Fact]
    public void CoverageScriptUsesConfiguredTraitExclusionsOnlyForCoverageCollection()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var productScript = File.ReadAllText(Path.Combine(root, "harness", "RunProductTests.ps1"));
        var coverageScript = File.ReadAllText(Path.Combine(root, "harness", "coverage", "GenerateCoverage.ps1"));

        Assert.Contains("$config.coverage.excludedTestTraits", coverageScript);
        Assert.Contains("@coverageFilterArgs --collect", coverageScript);
        Assert.Contains("$TraitMode", productScript);
        Assert.Contains("--filter", productScript);
        Assert.DoesNotContain("$config.coverage.excludedTestTraits", productScript);
    }

    [Fact]
    public void ProductTestWarningTraitsAreExactlyPerformanceAndMatchCoverageExclusions()
    {
        var productWarningTraits = Config.ProductTests.WarningTraits
            .Select(trait => $"{trait.Name}={trait.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var coverageExcludedTraits = Config.Coverage.ExcludedTestTraits
            .Select(trait => $"{trait.Name}={trait.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Category=Performance"], productWarningTraits);
        Assert.Equal(productWarningTraits, coverageExcludedTraits);
    }

    [Fact]
    public void CoverageAggregationCountersAreInitializedBeforeUse()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var coverageScript = File.ReadAllText(Path.Combine(root, "harness", "coverage", "GenerateCoverage.ps1"));

        foreach (string requiredInitializer in new[]
        {
            "$linesCovered = 0",
            "$linesValid = 0",
            "$branchesCovered = 0",
            "$branchesValid = 0",
        })
        {
            Assert.Contains(requiredInitializer, coverageScript);
        }
    }

    [Fact]
    public void HarnessBrokenStatusTakesPrecedenceOverNeedsGrillOutput()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var runHarness = File.ReadAllText(Path.Combine(root, "harness", "RunHarness.ps1"));
        string statusFunction = SliceFunction(runHarness, "function Set-FailingStatusFromOutput", "function Invoke-HarnessStep");

        int harnessBrokenIndex = statusFunction.IndexOf("HARNESS_BROKEN", StringComparison.Ordinal);
        int needsGrillIndex = statusFunction.IndexOf("NEEDS_GRILL:", StringComparison.Ordinal);
        int preserveHarnessBrokenIndex = statusFunction.IndexOf("$script:status -eq \"HARNESS_BROKEN\"", StringComparison.Ordinal);
        int preserveNeedsGrillIndex = statusFunction.IndexOf("$script:status -eq \"NEEDS_GRILL\"", StringComparison.Ordinal);

        Assert.True(harnessBrokenIndex >= 0, "Set-FailingStatusFromOutput must detect HARNESS_BROKEN.");
        Assert.True(needsGrillIndex >= 0, "Set-FailingStatusFromOutput must detect NEEDS_GRILL:.");
        Assert.True(preserveHarnessBrokenIndex >= 0, "Set-FailingStatusFromOutput must preserve an earlier HARNESS_BROKEN status.");
        Assert.True(preserveNeedsGrillIndex >= 0, "Set-FailingStatusFromOutput must preserve an earlier NEEDS_GRILL status unless a later step is HARNESS_BROKEN.");
        Assert.True(
            harnessBrokenIndex < needsGrillIndex,
            "HARNESS_BROKEN must take precedence when output contains both runner status tokens.");
        Assert.True(
            preserveHarnessBrokenIndex < needsGrillIndex,
            "HARNESS_BROKEN must stay final even if a later hard check prints NEEDS_GRILL:.");
        Assert.True(
            preserveNeedsGrillIndex > harnessBrokenIndex && preserveNeedsGrillIndex < needsGrillIndex,
            "NEEDS_GRILL must stay final across later ordinary hard failures, while still allowing HARNESS_BROKEN to override it.");
    }


    [Fact]
    public void HarnessKeepsCoverageAndStyleQualityOutOfHardBucket()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var runHarness = File.ReadAllText(Path.Combine(root, "harness", "RunHarness.ps1"));
        var buildScript = File.ReadAllText(Path.Combine(root, "harness", "BuildDeclaredBoundary.ps1"));
        string hardBucket = SliceFunction(runHarness, "function Invoke-HardBucket", "function Invoke-WarningBucket");
        string warningBucket = SliceFunction(runHarness, "function Invoke-WarningBucket", "function Write-HarnessRunSummary");

        Assert.Contains("build-declared-boundary", runHarness);
        Assert.Contains("harness/BuildDeclaredBoundary.ps1", runHarness);
        Assert.DoesNotContain("SomeEngine.slnx", SliceFunction(runHarness, "function Invoke-BuildOnce", "function Invoke-HardBucket"));
        Assert.DoesNotContain("SomeEngine.slnx", hardBucket);
        Assert.DoesNotContain("SomeEngine.slnx", warningBucket);
        Assert.Contains("$config.projects.productProjects", buildScript);
        Assert.Contains("$config.projects.testProjects", buildScript);
        Assert.Contains("$config.projects.buildSupportProjects", buildScript);
        Assert.Contains("$QualityAnalyzerEnabled", buildScript);
        Assert.Contains("$NoWarn", buildScript);
        Assert.Contains("$WarningsAsErrors", buildScript);
        Assert.Contains("$OutputRoot", buildScript);
        Assert.Contains("UseArtifactsOutput", buildScript);
        Assert.Contains("ArtifactsPath", buildScript);
        Assert.Contains("$LASTEXITCODE", buildScript);
        Assert.DoesNotContain("SomeEngine.slnx", buildScript);

        Assert.Contains("harness-execution", hardBucket);
        Assert.Contains("harness/TestHarnessExecution.ps1", hardBucket);
        Assert.Contains("quality-product-boundary", hardBucket);
        Assert.Contains("product-tests", hardBucket);
        Assert.Contains("TraitMode", hardBucket);
        Assert.Contains("Hard", hardBucket);
        Assert.Contains("BuildDeclaredBoundary.ps1", hardBucket);
        Assert.Contains("ProjectSet", hardBucket);
        Assert.Contains("Source", hardBucket);
        Assert.Contains("-QualityAnalyzerEnabled", hardBucket);
        Assert.Contains("-NoWarn", hardBucket);
        Assert.Contains("-OutputRoot", hardBucket);
        Assert.Contains("harness/artifacts/quality-hard", hardBucket);
        Assert.Contains("$script:qualitySoftRuleNoWarn", hardBucket);
        Assert.Contains("SE010%3BSE031%3BSE052", runHarness);
        Assert.DoesNotContain("-WarningsAsErrors", hardBucket);

        Assert.DoesNotContain("coverage-collect", hardBucket);
        Assert.DoesNotContain("coverage-gate", hardBucket);
        Assert.DoesNotContain("maintainability", hardBucket);
        Assert.DoesNotContain("quality-product-style", hardBucket);
        Assert.Contains("$hardBucketPassed = $true", hardBucket);
        Assert.Contains("$hardBucketPassed = $false", hardBucket);
        Assert.Contains("return $hardBucketPassed", hardBucket);

        Assert.Contains("quality-product-style", warningBucket);
        Assert.Contains("product-tests-performance", warningBucket);
        Assert.Contains("TraitMode", warningBucket);
        Assert.Contains("Warning", warningBucket);
        Assert.Contains("BuildDeclaredBoundary.ps1", warningBucket);
        Assert.Contains("ProjectSet", warningBucket);
        Assert.Contains("Source", warningBucket);
        Assert.Contains("-QualityAnalyzerEnabled", warningBucket);
        Assert.Contains("-NoWarn", warningBucket);
        Assert.Contains("-WarningsAsErrors", warningBucket);
        Assert.Contains("-OutputRoot", warningBucket);
        Assert.Contains("harness/artifacts/quality-style", warningBucket);
        Assert.Contains("coverage-collect", warningBucket);
        Assert.Contains("coverage-gate", warningBucket);
        Assert.Contains("maintainability", warningBucket);
        Assert.Contains("$script:qualityBoundaryRuleNoWarn", warningBucket);
        Assert.Contains("$script:qualityStyleRuleWarningsAsErrors", warningBucket);
        Assert.Contains("SE001%3BSE002%3BSE020%3BSE021%3BSE022%3BSE023%3BSE024%3BSE030", runHarness);

        Assert.Contains("hardChecksExecuted", runHarness);
        Assert.Contains("harness-warning-run.json", runHarness);
        Assert.Contains("harness-run.json", runHarness);
    }

    [Fact]
    public void DeclaredBoundaryBuildUsesOneGeneratedGraphInvocation()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "harness", "BuildDeclaredBoundary.ps1"));

        Assert.Contains(".declared-boundary-$ProjectSet-$PID.slnx", buildScript);
        Assert.Contains("WriteStartElement(\"Solution\")", buildScript);
        Assert.Contains("WriteStartElement(\"Project\")", buildScript);
        Assert.Contains("one MSBuild invocation", buildScript);
        Assert.Contains("\"-m\"", buildScript);
        Assert.Contains("Remove-Item -LiteralPath $solutionPath", buildScript);
        Assert.DoesNotContain("Building declared boundary project:", buildScript);
        Assert.Equal(1, CountOccurrences(buildScript, "& dotnet @buildArgs"));
    }

    [Fact]
    public void HarnessProcessExecutionDrainsBothStreamsBeforeWaitingForExit()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var processExecution = File.ReadAllText(Path.Combine(root, "harness", "ProcessExecution.ps1"));
        var executionTest = File.ReadAllText(Path.Combine(root, "harness", "TestHarnessExecution.ps1"));
        var fixture = File.ReadAllText(Path.Combine(root, "harness", "fixtures", "WriteProcessOutput.ps1"));

        int stdoutRead = processExecution.IndexOf("StandardOutput.ReadToEndAsync()", StringComparison.Ordinal);
        int stderrRead = processExecution.IndexOf("StandardError.ReadToEndAsync()", StringComparison.Ordinal);
        int waitForExit = processExecution.IndexOf("Process.WaitForExit()", StringComparison.Ordinal);
        Assert.True(stdoutRead >= 0 && stderrRead >= 0 && waitForExit >= 0);
        Assert.True(stdoutRead < waitForExit && stderrRead < waitForExit);

        Assert.Contains("4 parallel processes", executionTest);
        Assert.Contains("$lineCount = 4096", executionTest);
        Assert.Contains("30 second deadline", executionTest);
        Assert.Contains("failure exit code", executionTest);
        Assert.Contains("HARNESS_BROKEN:", executionTest);
        Assert.Contains("[Console]::Out.WriteLine", fixture);
        Assert.Contains("[Console]::Error.WriteLine", fixture);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string SliceFunction(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startMarker} must exist in RunHarness.ps1");

        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"{endMarker} must exist after {startMarker} in RunHarness.ps1");

        return source[start..end];
    }
}
