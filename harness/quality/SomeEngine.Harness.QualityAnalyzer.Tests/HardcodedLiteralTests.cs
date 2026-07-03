using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

public sealed class HardcodedLiteralTests
{
    private static string ConfigPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json"));

    [Fact]
    public async Task StringLiteralInMethod_Diagnostic()
    {
        var test = new CSharpAnalyzerTest<SomeEngineQualityAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { "class Sample { string M() => \"asset/path.png\"; }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerWarning("SE031").WithSpan(1, 30, 1, 46).WithArguments("\"asset/path.png\""),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ConstString_NoDiagnostic()
    {
        var test = new CSharpAnalyzerTest<SomeEngineQualityAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { "class Sample { private const string AssetPath = \"asset/path.png\"; }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ExceptionMessage_NoDiagnostic()
    {
        var test = new CSharpAnalyzerTest<SomeEngineQualityAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { "using System; class Sample { void M() => throw new InvalidOperationException(\"Human readable failure message.\"); }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task SmallStructuralNumber_NoDiagnostic()
    {
        var test = new CSharpAnalyzerTest<SomeEngineQualityAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { "class Sample { int M(int value) => value * 4; }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }
}

