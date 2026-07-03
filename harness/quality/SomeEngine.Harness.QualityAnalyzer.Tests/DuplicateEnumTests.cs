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
    public async Task SharedEnumMember_Diagnostic()
    {
        var test = new CSharpAnalyzerTest<SomeEngineQualityAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { "enum First { Shared } enum Second { Shared }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerWarning("SE052")
                        .WithSpan(1, 28, 1, 34)
                        .WithArguments("Second", "First", "Shared"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task SharedEnumMemberAcrossFiles_ReportsLaterDeclarationDeterministically()
    {
        var test = new CSharpAnalyzerTest<SomeEngineQualityAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources =
                {
                    ("A.cs", "enum First { Shared }"),
                    ("B.cs", "enum Second { Shared }"),
                },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerWarning("SE052")
                        .WithLocation("B.cs", 1, 6)
                        .WithArguments("Second", "First", "Shared"),
                },
            },
        };

        await test.RunAsync();
    }
}
