using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

public sealed class ReflectionUsageTests
{
    private static string ConfigPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json"));

    [Fact]
    public async Task SystemReflectionInvocation_Diagnostic()
    {
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { "class Sample { void M() { System.Reflection.MemberInfo[] members = typeof(string).GetMembers(); } }" },
                AdditionalFiles = { (Path.Combine("harness", "config.json"), File.ReadAllText(ConfigPath)) },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE030").WithSpan(1, 68, 1, 95).WithArguments("System.Type.GetMembers()"),
                },
            },
        };

        await test.RunAsync();
    }
}


