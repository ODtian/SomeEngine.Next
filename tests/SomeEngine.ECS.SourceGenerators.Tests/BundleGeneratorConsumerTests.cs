using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.SourceGenerators;
using Microsoft.CodeAnalysis;

namespace SomeEngine.ECS.SourceGenerators.Tests;

public sealed partial class BundleGeneratorConsumerTests
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
}

public struct GeneratedConsumerComponent : SomeEngine.ECS.IComponent
{
    public int Value;
}

public partial struct GeneratedConsumerBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public GeneratedConsumerComponent Value;
}
