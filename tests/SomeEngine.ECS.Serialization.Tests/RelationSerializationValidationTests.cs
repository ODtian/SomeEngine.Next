using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class RelationSerializationValidationTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(2_048)]
    public void DirectedHighFanoutRoundTripsEndpointsAndValues(int fanout)
    {
        var registry = new SerializationRegistry()
            .Register<HighFanoutRelation>()
            .RegisterRelation<HighFanoutRelation>();
        using var sourceWorld = new World();
        Entity source = sourceWorld.Create();
        var expectedTargets = new HashSet<Entity>();
        for (int i = 0; i < fanout; i++)
        {
            Entity target = sourceWorld.Create();
            expectedTargets.Add(target);
            sourceWorld.CreateRelation(
                source,
                target,
                new HighFanoutRelation { Value = i });
        }

        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(sourceWorld, stream);

        stream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(stream, registry);
        using RelationRead outgoing =
            loaded.GetOutgoingRelations<HighFanoutRelation>(source);
        Assert.Equal(fanout, outgoing.Count);

        var seenValues = new bool[fanout];
        for (int i = 0; i < outgoing.Count; i++)
        {
            RelationEntry entry = outgoing.Entries[i];
            Assert.Contains(entry.Other, expectedTargets);
            using RelationRead incoming = loaded.GetIncomingRelations<HighFanoutRelation>(entry.Other);
            Assert.Equal(1, incoming.Count);

            int value = loaded.Read<HighFanoutRelation>(entry.Edge).Value;
            Assert.InRange(value, 0, fanout - 1);
            Assert.False(seenValues[value]);
            seenValues[value] = true;
        }
        Assert.All(seenValues, Assert.True);
    }



    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct HighFanoutRelation : IComponent
    {
        public int Value;
    }
}
