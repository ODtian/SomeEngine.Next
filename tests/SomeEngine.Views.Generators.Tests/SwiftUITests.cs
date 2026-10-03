using Microsoft.CodeAnalysis;
using SomeEngine.Views.SwiftUI;

namespace SomeEngine.Views.Generators.Tests;

public sealed class SwiftUITests
{
    [Theory]
    [InlineData("public int Count { get; set; }")]
    [InlineData("public static partial int Count { get; set; }")]
    [InlineData("public partial int Count { get; init; }")]
    [InlineData("public partial int this[int index] { get; set; }")]
    public async Task StateDeclarationsRequireInstancePartialGetAndSetProperties(string declaration)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.SwiftUI;
            using SomeEngine.ECS.Components;
            public partial record struct Counter : IView, ISparseComponent
            {
                [State] {{declaration}}
                public BuildTask Build() => default;
            }
            """, true, new StateAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV015" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "CS8785");
    }
}
