using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

public sealed class VarUsageTests
{
    private static string ConfigPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json"));

    [Fact]
    public async Task ElementAccessVar_NoDiagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { string M() { var items = new[] { \"a\" }; var item = items[0]; return item; } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task NonObviousExpressionVar_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { int M(int left, int right) { var total = left + right; return total; } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerWarning("SE010").WithSpan(1, 45, 1, 48),
                },
            },
        };

        await test.RunAsync();
    }
}
