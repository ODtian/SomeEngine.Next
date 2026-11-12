using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Job;

namespace SomeEngine.ECS.Tests;

public sealed class MembershipChangesTests
{
    [Fact]
    public void AcceptingAnEarlierBatchPreservesLaterChangesAndDestroyedIdentities()
    {
        using var world = new World();
        Query query = world.Query(new QueryDescription().WithStored<Position>().Without<Velocity>());
        using EventReader<MembershipChange> changes = world.WatchMembership(query);
        Entity entity = world.Create(new Position());
        MembershipChange first;
        ulong firstThrough;
        using (EventRead<MembershipChange> read = changes.Open(changes.Capture()))
        {
            first = Assert.Single(read.First.ToArray());
            firstThrough = read.Through;
        }
        world.Add(entity, new Velocity());
        changes.Advance(firstThrough);
        ulong secondThrough;
        using (EventRead<MembershipChange> read = changes.Open(changes.Capture()))
        {
            MembershipChange second = Assert.Single(read.First.ToArray());
            Assert.Equal(entity, second.Entity);
            Assert.Equal(MembershipChangeKind.Removed, second.Kind);
            secondThrough = read.Through;
        }
        changes.Advance(secondThrough);
        world.Set(entity, new Position { X = 12 });
        using (EventRead<MembershipChange> read = changes.Open(changes.Capture())) Assert.Equal(0, read.Count);
        world.Remove<Velocity>(entity);
        world.Destroy(entity);
        Entity replacement = world.Create(new Position());
        ulong finalThrough;
        using (EventRead<MembershipChange> read = changes.Open(changes.Capture()))
        {
            Assert.Equal([entity, entity, replacement], read.First.ToArray().Select(change => change.Entity));
            Assert.Equal([MembershipChangeKind.Added, MembershipChangeKind.Removed, MembershipChangeKind.Added],
                read.First.ToArray().Select(change => change.Kind));
            Assert.Equal(entity, first.Entity);
            finalThrough = read.Through;
        }
        changes.Advance(finalThrough);
        using EventRead<MembershipChange> final = changes.Open(changes.Capture());
        Assert.Equal(0, final.Count);
    }

    [Fact]
    public void FailedCandidateCommandBatchPreservesTheSourceMembershipEvents()
    {
        using var world = new World();
        Entity entity = world.Create(new Position());
        Query query = world.Query(new QueryDescription().WithStored<Position>().Without<Velocity>());
        using EventReader<MembershipChange> changes = world.WatchMembership(query);
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using (var commands = new CommandBuffer(candidate))
        {
            commands.Add(entity, new Velocity());
            commands.Add(Entity.Null, new Position());
            Assert.Throws<ArgumentException>(commands.Playback);
        }
        using (EventRead<MembershipChange> read = changes.Open(changes.Capture())) Assert.Equal(0, read.Count);
        Assert.False(world.Has<Velocity>(entity));
        using (var commands = new CommandBuffer(world))
        {
            commands.Add(entity, new Velocity());
            commands.Playback();
        }
        using EventRead<MembershipChange> published = changes.Open(changes.Capture());
        MembershipChange change = Assert.Single(published.First.ToArray());
        Assert.Equal(entity, change.Entity);
        Assert.Equal(MembershipChangeKind.Removed, change.Kind);
    }

    [Fact]
    public void ObserversAcceptIndependently()
    {
        using var world = new World();
        Query query = world.Query(new QueryDescription());
        using EventReader<MembershipChange> first = world.WatchMembership(query);
        using EventReader<MembershipChange> second = world.WatchMembership(query);
        Entity entity = world.Create();
        ulong through;
        using (EventRead<MembershipChange> read = first.Open()) through = read.Through;
        first.Advance(through);
        using (EventRead<MembershipChange> read = first.Open()) Assert.Equal(0, read.Count);
        using (EventRead<MembershipChange> read = second.Open())
            Assert.Equal(entity, Assert.Single(read.First.ToArray()).Entity);
        second.Advance(through);
        using EventRead<MembershipChange> remaining = second.Open();
        Assert.Equal(0, remaining.Count);
    }
}
