using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators.Tests;

public sealed class AsyncCallTests
{
    [Theory]
    [InlineData("Action<int, Func<int, CancellationToken, BuildTask>> launch = Scope.Launch<int>;")]
    [InlineData("Func<int, Func<int, CancellationToken, ValueTask<int>>, Resource<int>> resource = Scope.Resource<int, int>;")]
    public async Task MethodGroupsRequireDirectAsyncCalls(string declaration)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using SomeEngine.Views;
            public static class AsyncCalls
            {
                public static void Declare()
                {
                    {{declaration}}
                }
            }
            """, true, new ViewAnalyzer());

        Assert.Contains(diagnostics, value => value.Id == "SEV004" && value.Severity == DiagnosticSeverity.Error);
    }
}
