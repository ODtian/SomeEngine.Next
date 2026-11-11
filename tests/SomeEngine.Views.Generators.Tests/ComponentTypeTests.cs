using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ComponentTypeTests
{
    [Theory]
    [InlineData("world.Add(entity, 42);")]
    [InlineData("world.Set(entity, 42);")]
    [InlineData("world.AddSparse(entity, 42);")]
    [InlineData("world.ReplaceSparse(entity, 42);")]
    [InlineData("world.HasSparse<int>(entity);")]
    public async Task StructValuesUseTheNativeComponentEntries(string access)
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.ECS;
            using SomeEngine.ECS.Entities;
            public static class ComponentChecks
            {
                public static void Check(World world, Entity entity)
                {
            """ + access + """
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
