using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.SourceGen;
using Microsoft.CodeAnalysis;

namespace SomeEngine.ECS.SourceGen.Tests;

public sealed partial class SourceGenConsumerTests
{
    [Fact]
    public void BundleGeneratorIsIncrementalGenerator()
    {
        Assert.NotNull(new BundleGenerator());
    }

    [Fact]
    public void GeneratedBundleCanSpawnEntity()
    {
        var world = new World();
        var entity = world.Spawn(new GeneratedConsumerBundle { Value = new GeneratedConsumerComponent { Value = 9 } });

        Assert.Equal(9, world.Read<GeneratedConsumerComponent>(entity).Value);
    }

    [Fact]
    public void GeneratedJobWritesTheSparseValueOfItsMatchingEntity()
    {
        using var world = new World();
        var first = world.Create(new GeneratedConsumerComponent { Value = 9 });
        var second = world.Create(new GeneratedConsumerComponent { Value = 20 });
        world.SetSparse(first, new GeneratedSparseValue { Value = 4 });

        new GeneratedSparseJob().Execute(world);

        Assert.Equal(13, world.ReadSparse<GeneratedSparseValue>(first).Value);
        Assert.False(world.HasSparse<GeneratedSparseValue>(second));
    }
}

public struct GeneratedConsumerComponent : SomeEngine.ECS.IComponent
{
    public int Value;
}

public partial struct GeneratedConsumerBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public GeneratedConsumerComponent Value;
}

public struct GeneratedSparseValue : ISparseComponent
{
    public int Value;
}

public partial struct GeneratedSparseJob : SomeEngine.ECS.Systems.IJobEntity
{
    public void Execute(in GeneratedConsumerComponent component, ref GeneratedSparseValue sparse)
        => sparse.Value += component.Value;
}
