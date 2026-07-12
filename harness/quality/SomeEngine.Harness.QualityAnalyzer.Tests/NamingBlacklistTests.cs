using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

public sealed class NamingBlacklistTests
{
    private static string ConfigPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json"));

    [Fact]
    public async Task ClassEndingInPlan_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class FooPlan { }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE001")
                        .WithSpan(1, 7, 1, 14)
                        .WithArguments("FooPlan"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ClassEndingInRun_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class FooRun { }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE001")
                        .WithSpan(1, 7, 1, 13)
                        .WithArguments("FooRun"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ClassEndingInProgram_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class FooProgram { }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE001")
                        .WithSpan(1, 7, 1, 17)
                        .WithArguments("FooProgram"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task MethodEndingInPlan_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { void WorkPlan() { } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE002")
                        .WithSpan(1, 21, 1, 29)
                        .WithArguments("WorkPlan"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task MethodEndingInRun_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { void WorkRun() { } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE002")
                        .WithSpan(1, 21, 1, 28)
                        .WithArguments("WorkRun"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task MethodEndingInProgram_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { void WorkProgram() { } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE002")
                        .WithSpan(1, 21, 1, 32)
                        .WithArguments("WorkProgram"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ProgramClass_NoDiagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Program { static void Main() {} }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }
}
