using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators.Tests;

public sealed class AsyncCallTests
{
    [Fact]
    public async Task ASolidComponentUsesTheSharedResourceWhenItsSourceChanges()
    {
        bool updated = await CompilerChecks.Run("""
            using System.Threading.Tasks;
            using SomeEngine.ECS;
            using SomeEngine.ECS.Entities;
            using SomeEngine.Views;
            using SomeEngine.Views.Solid;
            using SomeEngine.Views.Tests;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    var source = new State<int>(1);
                    Entity target = frames.World.CreateEntity(new Output(0));
                    Views.Spawn(frames.World, Entity.Null, new Host(source, target));
                    frames.Frame();
                    bool first = frames.World.Read<Output>(target).Value == 10;
                    source.Value = 2;
                    frames.Frame();
                    return first && frames.World.Read<Output>(target).Value == 20;
                }
            }

            public static partial class Content
            {
                [Component]
                public static void Fetch(State<int> source, Entity target)
                {
                    Scope.Compute(() =>
                    {
                        Resource<int> resource = Scope.Resource(source.Value,
                            static (value, cancel) => ValueTask.FromResult(value * 10));
                        Scope.Write<Output>(target) = new Output(resource.Value);
                    });
                }
            }

            public readonly record struct Host(State<int> Source, Entity Target) : IView, IComponent
            {
                public BuildTask Build() { Content.Fetch(Source, Target); return default; }
            }

            public readonly record struct Output(int Value) : IComponent;
            """);

        Assert.True(updated);
    }

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
