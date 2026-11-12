using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Core.Tests.ECS;

public sealed class WorldMembershipEventsTests
{
    private struct Member : IComponent;
    private struct Visible : IEnableableComponent;
    private struct Detail : IComponent;
    private struct Extra : IComponent;

    [Fact]
    public void RegistrationStartsAtTheCurrentMembershipTail()
    {
        using var world = new World();
        world.Create(new Member());
        Query query = world.Query<Member>();
        using EventReader<MembershipChange> reader = world.WatchMembership(query);

        Entity added = world.Create(new Member());

        using EventRead<MembershipChange> changes = reader.Open();
        MembershipChange change = Assert.Single(changes.First.ToArray());
        Assert.Equal(added, change.Entity);
        Assert.Equal(MembershipChangeKind.Added, change.Kind);
    }

    [Fact]
    public void EachReaderRetainsItsOwnUnconsumedMembershipInterval()
    {
        using var world = new World();
        Query query = world.Query<Member>();
        using EventReader<MembershipChange> fast = world.WatchMembership(query);
        using EventReader<MembershipChange> slow = world.WatchMembership(query);
        Entity first = world.Create(new Member());
        ulong firstThrough;
        using (EventRead<MembershipChange> changes = fast.Open()) firstThrough = changes.Through;
        fast.Advance(firstThrough);

        Entity second = world.Create(new Member());
        world.Remove<Member>(first);

        using EventRead<MembershipChange> fastChanges = fast.Open();
        using EventRead<MembershipChange> slowChanges = slow.Open();
        Assert.Equal([second, first], fastChanges.First.ToArray().Select(change => change.Entity));
        Assert.Equal([first, second, first], slowChanges.First.ToArray().Select(change => change.Entity));
        Assert.Equal(MembershipChangeKind.Removed, fastChanges.First[^1].Kind);
    }

    [Fact]
    public void BatchCreationAndDestructionPublishEverySelectedIdentity()
    {
        using var world = new World(chunkCapacity: 64);
        Query query = world.Query<Member>();
        using EventReader<MembershipChange> reader = world.WatchMembership(query);
        var entities = new Entity[130];
        world.Create(entities, new Member(), new Detail(), new Extra());
        ulong through;
        using (EventRead<MembershipChange> changes = reader.Open())
        {
            Assert.Equal(entities, changes.First.ToArray().Select(change => change.Entity));
            Assert.All(changes.First.ToArray(), change => Assert.Equal(MembershipChangeKind.Added, change.Kind));
            through = changes.Through;
        }
        reader.Advance(through);

        world.Destroy(entities);

        using EventRead<MembershipChange> removed = reader.Open();
        Assert.Equal(entities, removed.First.ToArray().Select(change => change.Entity));
        Assert.All(removed.First.ToArray(), change => Assert.Equal(MembershipChangeKind.Removed, change.Kind));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void FirstDisablePreservesStoredMembersAndPublishesTheEnabledExit()
    {
        using var world = new World();
        Entity entity = world.Create(new Visible());
        Query stored = world.Query(new QueryDescription().WithStored<Visible>());
        Query enabled = world.Query<Visible>();
        using EventReader<MembershipChange> storedReader = world.WatchMembership(stored);
        using EventReader<MembershipChange> enabledReader = world.WatchMembership(enabled);
        ulong storedVersion = stored.MembershipVersion;
        ulong enabledVersion = enabled.MembershipVersion;

        world.Disable<Visible>(entity);

        Assert.Equal(storedVersion, stored.MembershipVersion);
        Assert.NotEqual(enabledVersion, enabled.MembershipVersion);
        Assert.Equal(1, stored.Count);
        Assert.Equal(0, enabled.Count);
        using EventRead<MembershipChange> storedChanges = storedReader.Open();
        using EventRead<MembershipChange> enabledChanges = enabledReader.Open();
        Assert.Equal(0, storedChanges.Count);
        MembershipChange change = Assert.Single(enabledChanges.First.ToArray());
        Assert.Equal(entity, change.Entity);
        Assert.Equal(MembershipChangeKind.Removed, change.Kind);
    }

    [Fact]
    public void OrdinaryPlaybackPublishesItsCompletedPrefixBeforeACommandFails()
    {
        using var world = new World();
        Entity first = world.Create(new Member());
        Entity survivor = world.Create(new Member());
        Query query = world.Query<Member>();
        using EventReader<MembershipChange> reader = world.WatchMembership(query);
        using var commands = new CommandBuffer(world);
        commands.Destroy(first);
        commands.Add(Entity.Null, new Detail());

        Assert.Throws<ArgumentException>(commands.Playback);

        Assert.Equal(1, query.Count);
        Assert.True(world.IsAlive(survivor));
        using EventRead<MembershipChange> changes = reader.Open();
        MembershipChange change = Assert.Single(changes.First.ToArray());
        Assert.Equal(first, change.Entity);
        Assert.Equal(MembershipChangeKind.Removed, change.Kind);
    }

    [Fact]
    public void DeclaredStructureJobsPublishMembershipBeforeTheirCompletion()
    {
        using var scheduler = new Scheduler(new JobConfiguration
        { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var jobs = new WorldJobs(scheduler);
        using var world = new World(execution: jobs);
        using EventReader<MembershipChange> reader = world.WatchMembership(world.Query<Member>());

        scheduler.Schedule(new CreateMember(world), jobs.StructureWrite).Complete();

        using EventRead<MembershipChange> changes = reader.Open();
        MembershipChange change = Assert.Single(changes.First.ToArray());
        Assert.True(world.IsAlive(change.Entity));
        Assert.Equal(MembershipChangeKind.Added, change.Kind);
    }

    private readonly struct CreateMember(World world) : IJob
    {
        public void Execute() => world.Create(new Member());
    }
}
