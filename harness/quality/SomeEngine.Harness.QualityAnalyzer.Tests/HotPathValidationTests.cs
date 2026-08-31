using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

public sealed class HotPathValidationTests
{
    [Fact]
    public async Task SubsystemValidationInsideRenderLoopIsRejected()
    {
        const string source =
            "namespace SomeEngine.RenderGraph; class Sample { void Run() { for (;;) { ValidateRange(); } } void ValidateRange() { } }";
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { source },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE032")
                        .WithSpan(1, 74, 1, 89)
                        .WithArguments("SomeEngine.RenderGraph.Sample.ValidateRange()"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ThrowIfInsideGraphicsLoopIsRejected()
    {
        const string source =
            "namespace SomeEngine.Graphics; class Sample { void Run(bool disposed) { while (true) { System.ObjectDisposedException.ThrowIf(disposed, this); } } }";
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { source },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE032")
                        .WithSpan(1, 88, 1, 142)
                        .WithArguments("System.ObjectDisposedException.ThrowIf(bool, object)"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidationInsideRenderSubsystemLoopIsRejected()
    {
        const string source =
            "namespace SomeEngine.Render.Cluster; class Sample { void Run() { foreach (int value in new int[1]) { ValidateItem(value); } } void ValidateItem(int value) { } }";
        var test = new OfflineAnalyzerTest
        {
            TestState =
            {
                Sources = { source },
                ExpectedDiagnostics =
                {
                    DiagnosticResult.CompilerError("SE032")
                        .WithSpan(1, 102, 1, 121)
                        .WithArguments("SomeEngine.Render.Cluster.Sample.ValidateItem(int)"),
                },
            },
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task ExternalBoundaryValidationOutsideLoopRemainsAllowed()
    {
        const string source =
            "namespace SomeEngine.Render; class Sample { void Import(int value) { System.ArgumentOutOfRangeException.ThrowIfNegative(value); } }";
        var test = new OfflineAnalyzerTest
        {
            TestState = { Sources = { source } },
        };

        await test.RunAsync();
    }
}
