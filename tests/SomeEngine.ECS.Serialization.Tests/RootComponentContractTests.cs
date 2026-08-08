using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class RootComponentContractTests
{
    [Fact]
    public void RootOnlyComponent_RegistersAndRoundTripsAsComponentAndWorldData()
    {
        var registry = new SerializationRegistry()
            .Register<RootOnlySerializedValue>();
        using var componentStream = new MemoryStream();

        WorldSerializer.WriteComponent(
            componentStream,
            new RootOnlySerializedValue { Value = 31 },
            registry);
        componentStream.Position = 0;
        RootOnlySerializedValue component =
            WorldSerializer.ReadComponent<RootOnlySerializedValue>(componentStream, registry);
        Assert.Equal(31, component.Value);

        using var source = new World();
        Entity entity = source.CreateEntity(new RootOnlySerializedValue { Value = 32 });
        using var worldStream = new MemoryStream();
        WorldSerializer.WriteWorld(worldStream, source, registry);

        worldStream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(worldStream, registry);
        Assert.True(loaded.IsAlive(entity));
        Assert.Equal(32, loaded.Read<RootOnlySerializedValue>(entity).Value);
    }

    [Fact]
    public void RootOnlyRelationPayload_RegistersTopologyAndRoundTrips()
    {
        var registry = new SerializationRegistry()
            .Register<RootOnlySerializedRelation>()
            .RegisterRelationTopology<RootOnlySerializedRelation>();
        using var sourceWorld = new World();
        Entity source = sourceWorld.CreateEntity();
        Entity target = sourceWorld.CreateEntity();
        RelationEdge<RootOnlySerializedRelation> edge = sourceWorld.CreateRelation(
            source,
            target,
            new RootOnlySerializedRelation { Value = 41 });
        using var stream = new MemoryStream();

        WorldSerializer.WriteWorld(stream, sourceWorld, registry);
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(stream, registry);

        RelationEdge<RootOnlySerializedRelation> loadedEdge = Assert.Single(
            loaded.GetRelationEdgesBetween<RootOnlySerializedRelation>(source, target).ToArray());
        Assert.Equal(edge, loadedEdge);
        Assert.Equal(41, loaded.Read<RootOnlySerializedRelation>(loadedEdge.Entity).Value);
    }

    private struct RootOnlySerializedValue : global::SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct RootOnlySerializedRelation : global::SomeEngine.ECS.IComponent
    {
        public int Value;
    }

}
