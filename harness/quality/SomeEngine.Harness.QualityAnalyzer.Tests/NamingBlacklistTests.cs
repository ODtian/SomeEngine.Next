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
    public async Task ClassEndingInPlan_NoDiagnosticByDefault()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class FooPlan { }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ClassEndingInRun_NoDiagnosticByDefault()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class FooRun { }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ClassEndingInProgram_NoDiagnosticByDefault()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class FooProgram { }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task MethodEndingInPlan_NoDiagnosticByDefault()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { void WorkPlan() { } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task MethodEndingInRun_NoDiagnosticByDefault()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { void WorkRun() { } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task MethodEndingInProgram_NoDiagnosticByDefault()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { void WorkProgram() { } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
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
