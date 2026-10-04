using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ComponentTypeTests
{
    [Theory]
    [InlineData("world.Add(entity, 42);")]
    [InlineData("world.Replace(entity, 42);")]
    [InlineData("world.AddSparse(entity, 42);")]
    [InlineData("world.ReplaceSparse(entity, 42);")]
    [InlineData("world.HasSparse<int>(entity);")]
    public async Task ComponentAccessRequiresTheDeclaredStorageType(string access)
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

        Assert.Contains(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
            diagnostic.Id is "CS0314" or "CS0315");
    }
}
