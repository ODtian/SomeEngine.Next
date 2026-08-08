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
    public void DirectedHighFanout_RoundTripsWithLinearValidationVisits(int fanout)
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

        RelationTypeState<HighFanoutRelation> state = Assert.IsType<RelationTypeState<HighFanoutRelation>>(
            sourceWorld.RelationGraph.PrepareSerializationWrite<HighFanoutRelation>());
        object backing = state.BackingIdentity;
        int detachCount = state.DetachCount;
        long fullCloneCount = state.FullCloneCount;

        long recordCount = state.PrepareSerializationWrite(sourceWorld.PublishedStructureRoot);
        RelationSerializationValidationDiagnostics diagnostics =
            state.SerializationValidationDiagnostics;
        Assert.Equal(fanout, recordCount);
        Assert.Equal(1, diagnostics.CompletedValidationCount);
        Assert.Equal(checked(2L * fanout), diagnostics.EdgeVisits);
        Assert.Equal(checked((long)fanout + 1), diagnostics.ShardVisits);
        Assert.Equal(checked(2L * fanout), diagnostics.MembershipVisits);
        Assert.Same(backing, state.BackingIdentity);
        Assert.Equal(detachCount, state.DetachCount);
        Assert.Equal(fullCloneCount, state.FullCloneCount);

        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, sourceWorld, registry);
        Assert.Same(
            backing,
            sourceWorld.RelationGraph.StateBackingIdentity<HighFanoutRelation>());
        Assert.Equal(
            detachCount,
            sourceWorld.RelationGraph.StateDetachCount<HighFanoutRelation>());
        Assert.Equal(
            fullCloneCount,
            sourceWorld.RelationGraph.StateFullCloneCount<HighFanoutRelation>());

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

    [Fact]
    public void DuplicateMembership_FailsBeforeWritingAnyBytes()
    {
        var registry = new SerializationRegistry()
            .Register<HighFanoutRelation>()
            .RegisterRelationTopology<HighFanoutRelation>();
        using var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        world.CreateRelation(source, target, new HighFanoutRelation { Value = 1 });

        RelationTypeState<HighFanoutRelation> state = Assert.IsType<RelationTypeState<HighFanoutRelation>>(
            world.RelationGraph.PrepareSerializationWrite<HighFanoutRelation>());
        RelationGeneration<HighFanoutRelation> generation = state.SerializationGeneration;
        RelationAdjacencyShard<HighFanoutRelation> original = generation.Outgoing[source];
        var duplicateEntries = new RelationAdjacencyEntry<HighFanoutRelation>[2];
        duplicateEntries[0] = original.Entries[0];
        duplicateEntries[1] = original.Entries[0];
        generation.Outgoing[source] =
            new UnorderedRelationAdjacencyShard<HighFanoutRelation>(duplicateEntries);

        using var stream = new MemoryStream();
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                WorldSerializer.WriteWorld(stream, world, registry));
            Assert.Contains("repeats edge", error.Message);
            Assert.Equal(0, stream.Length);
        }
        finally
        {
            generation.Outgoing[source] = original;
        }
    }

    [Fact]
    public void MissingMembership_FailsBeforeWritingAnyBytes()
    {
        var registry = new SerializationRegistry()
            .Register<HighFanoutRelation>()
            .RegisterRelationTopology<HighFanoutRelation>();
        using var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        RelationEdge<HighFanoutRelation> edge = world.CreateRelation(
            source,
            target,
            new HighFanoutRelation { Value = 1 });

        RelationTypeState<HighFanoutRelation> state = Assert.IsType<RelationTypeState<HighFanoutRelation>>(
            world.RelationGraph.PrepareSerializationWrite<HighFanoutRelation>());
        RelationGeneration<HighFanoutRelation> generation = state.SerializationGeneration;
        RelationAdjacencyShard<HighFanoutRelation> original = generation.Incoming[target];
        Assert.True(generation.Incoming.Remove(target));

        using var stream = new MemoryStream();
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                WorldSerializer.WriteWorld(stream, world, registry));
            Assert.Contains(
                $"Relation edge {edge.Entity} has 0 incoming adjacency memberships",
                error.Message);
            Assert.Equal(0, stream.Length);
        }
        finally
        {
            generation.Incoming[target] = original;
        }
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct HighFanoutRelation : IComponent
    {
        public int Value;
    }
}
