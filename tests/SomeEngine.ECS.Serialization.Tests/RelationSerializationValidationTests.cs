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
            .RegisterRelationTopology<HighFanoutRelation>();
        using var sourceWorld = new World();
        Entity source = sourceWorld.CreateEntity();
        var expectedTargets = new HashSet<Entity>();
        for (int i = 0; i < fanout; i++)
        {
            Entity target = sourceWorld.CreateEntity();
            expectedTargets.Add(target);
            sourceWorld.CreateRelation(
                source,
                target,
                new HighFanoutRelation { Value = i });
        }

        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, sourceWorld, registry);

        stream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(stream, registry);
        RelationAdjacencySnapshot<HighFanoutRelation> outgoing =
            loaded.GetOutgoingRelations<HighFanoutRelation>(source);
        Assert.Equal(fanout, outgoing.Count);

        var seenValues = new bool[fanout];
        for (int i = 0; i < outgoing.Count; i++)
        {
            RelationAdjacencyEntry<HighFanoutRelation> entry = outgoing.Entries[i];
            Assert.Contains(entry.OtherEndpoint, expectedTargets);
            Assert.Equal(
                1,
                loaded.GetIncomingRelations<HighFanoutRelation>(entry.OtherEndpoint).Count);

            int value = loaded.Read<HighFanoutRelation>(entry.Edge.Entity).Value;
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
