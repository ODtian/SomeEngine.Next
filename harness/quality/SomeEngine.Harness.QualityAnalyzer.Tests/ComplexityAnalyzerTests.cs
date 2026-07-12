using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

public sealed class ComplexityAnalyzerTests
{
    private static string ConfigPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json"));

    [Fact]
    public async Task CyclomaticComplexity_Diagnostic()
    {
        string branches = string.Join(
            " ",
            Enumerable.Range(0, 13).Select(index => $"if (v == {index}) return {index};"));
        var test = NewAnalyzerTest($"class Sample {{ int M(int v) {{ {branches} return v; }} }}");
        test.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("SE020")
                .WithSpan(1, 20, 1, 21)
                .WithArguments("M", 14, 12));

        await test.RunAsync();
    }

    [Fact]
    public async Task MethodLength_Diagnostic()
    {
        string body = string.Join(
            "\n",
            Enumerable.Repeat("        ", 61));
        var test = NewAnalyzerTest(
            $$"""
            class Sample
            {
                int M()
                {
            {{body}}
                    return default;
                }
            }
            """);
        test.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("SE021")
                .WithSpan(3, 9, 3, 10)
                .WithArguments("M", 64, 60));

        await test.RunAsync();
    }

    [Fact]
    public async Task MethodsPerClass_Diagnostic()
    {
        string methods = string.Join(
            " ",
            Enumerable.Range(0, 26).Select(index => $"void M{index}() {{ }}"));
        var test = NewAnalyzerTest($"class Sample {{ {methods} }}");
        test.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("SE022")
                .WithSpan(1, 7, 1, 13)
                .WithArguments("Sample", 26, 25));

        await test.RunAsync();
    }

    [Fact]
    public async Task FieldsPerClass_Diagnostic()
    {
        string fields = string.Join(
            " ",
            Enumerable.Range(0, 21).Select(index => $"int field{index};"));
        var test = NewAnalyzerTest($"class Sample {{ {fields} }}");
        test.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("SE023")
                .WithSpan(1, 7, 1, 13)
                .WithArguments("Sample", 21, 20));

        await test.RunAsync();
    }

    [Fact]
    public async Task CoupledTypes_Diagnostic()
    {
        string types = string.Join(" ", Enumerable.Range(0, 9).Select(index => $"class T{index} {{ }}"));
        string references = string.Join(
            "\n",
            Enumerable.Range(0, 9).Select(index => $"        T{index} value{index} = new T{index}();"));
        var test = NewAnalyzerTest(
            $$"""
            {{types}}
            class Sample
            {
                void M()
                {
            {{references}}
                }
            }
            """);
        test.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("SE024")
                .WithSpan(4, 10, 4, 11)
                .WithArguments("M", 9, 8));

        await test.RunAsync();
    }

    [Fact]
    public async Task AcceptedCheckpointDiagnostic_IsExactAndMetricBounded()
    {
        string branches = string.Join(
            " ",
            Enumerable.Range(0, 13).Select(index => $"if (v == {index}) return {index};"));
        string config = File.ReadAllText(ConfigPath).Replace(
            "\"maxCoupledTypes\": 8",
            "\"maxCoupledTypes\": 8, " +
            "\"acceptedCheckpointCommit\": \"c0ac382e\", " +
            "\"acceptedCheckpointDiagnostics\": [{" +
            "\"id\":\"SE020\",\"assembly\":\"TestProject\"," +
            "\"path\":\"Test0.cs\",\"line\":1,\"symbol\":\"M\"," +
            "\"maximumObserved\":14,\"reason\":\"accepted checkpoint\"}]",
            System.StringComparison.Ordinal);
        var test = NewAnalyzerTest(
            $"class Sample {{ int M(int v) {{ {branches} return v; }} }}",
            config);

        await test.RunAsync();
    }

    [Fact]
    public async Task AcceptedCheckpointDiagnostic_DoesNotHideRegression()
    {
        string branches = string.Join(
            " ",
            Enumerable.Range(0, 14).Select(index => $"if (v == {index}) return {index};"));
        string config = File.ReadAllText(ConfigPath).Replace(
            "\"maxCoupledTypes\": 8",
            "\"maxCoupledTypes\": 8, " +
            "\"acceptedCheckpointCommit\": \"c0ac382e\", " +
            "\"acceptedCheckpointDiagnostics\": [{" +
            "\"id\":\"SE020\",\"assembly\":\"TestProject\"," +
            "\"path\":\"Test0.cs\",\"line\":1,\"symbol\":\"M\"," +
            "\"maximumObserved\":14,\"reason\":\"accepted checkpoint\"}]",
            System.StringComparison.Ordinal);
        var test = NewAnalyzerTest(
            $"class Sample {{ int M(int v) {{ {branches} return v; }} }}",
            config);
        test.ExpectedDiagnostics.Add(
            DiagnosticResult.CompilerError("SE020")
                .WithSpan(1, 20, 1, 21)
                .WithArguments("M", 15, 12));

        await test.RunAsync();
    }

    private static CSharpAnalyzerTest<SomeEngineQualityAnalyzer, DefaultVerifier> NewAnalyzerTest(
        string source,
        string? config = null)
    {
        var test = new OfflineAnalyzerTest();
        test.TestState.Sources.Add(source);
        test.TestState.AdditionalFiles.Add(
            (Path.Combine("harness", "config.json"), config ?? File.ReadAllText(ConfigPath)));
        return test;
    }
}
