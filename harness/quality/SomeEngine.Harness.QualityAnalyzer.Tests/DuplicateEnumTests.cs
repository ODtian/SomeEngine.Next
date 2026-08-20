using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

public sealed class DuplicateEnumTests
{
    private static string ConfigPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json"));

    [Fact]
    public async Task SingularAndPluralEnumWithSharedMember_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "enum ThingType { Shared } enum ThingTypes { Shared }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerWarning("SE052")
                        .WithSpan(1, 32, 1, 42)
                        .WithArguments("ThingTypes", "ThingType", "Shared"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task SingularAndPluralEnumAcrossFiles_ReportsLaterDeclarationDeterministically()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources =
                {
                    ("A.cs", "enum ThingType { Shared }"),
                    ("B.cs", "enum ThingTypes { Shared }"),
                },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerWarning("SE052")
                        .WithLocation("B.cs", 1, 6)
                        .WithArguments("ThingTypes", "ThingType", "Shared"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task UnrelatedEnumsWithCommonMembers_NoDiagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources =
                {
                    "enum QueueType { None, Copy } enum PipelineStage { None, Copy }",
                },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }
}
