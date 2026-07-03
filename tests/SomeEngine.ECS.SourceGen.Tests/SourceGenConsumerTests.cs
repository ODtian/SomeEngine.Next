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

        Assert.Equal(9, world.Get<GeneratedConsumerComponent>(entity).Value);
    }
}

public struct GeneratedConsumerComponent : SomeEngine.ECS.Components.IComponent
{
    public int Value;
}

public partial struct GeneratedConsumerBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public GeneratedConsumerComponent Value;
}
