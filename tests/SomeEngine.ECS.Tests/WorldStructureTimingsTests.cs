using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.Job;

namespace SomeEngine.ECS.Tests;

public sealed class WorldStructureTimingsTests
{
    [Fact]
    public void ExplicitTimingCountsTheOuterWriteAndPreservesPlaybackMembershipFacts()
    {
        using var world = new World();
        Query query = world.Query(new QueryDescription().With<Position>());
        using EventReader<MembershipChange> reader = world.WatchMembership(query);
        using (var commands = new CommandBuffer(world))
        {
            commands.Create(new Position(1));
            commands.Playback();
        }
        Assert.Equal(default, world.StructureTimings.Read());

        world.StructureTimings.Enabled = true;
        using (world.OpenStructureWrite())
        using (var commands = new CommandBuffer(world))
        {
            commands.Create(new Position(2));
            commands.Playback();
        }

        WorldStructureTimes times = world.StructureTimings.Read();
        Assert.Equal(1, times.StructureWriteCount);
        Assert.Equal(1, times.CommandPlaybackCount);
        Assert.True(times.MembershipChangeBatches > 0);
        Assert.True(times.StructureWriteTicks >= times.CommandPlaybackTicks);
        Assert.Equal(2, query.Count);
        using EventRead<MembershipChange> changes = reader.Open();
        Assert.Equal(2, changes.Count);
        foreach (MembershipChange change in changes) Assert.Equal(MembershipChangeKind.Added, change.Kind);
    }

    private readonly record struct Position(int Value) : IComponent;
}
