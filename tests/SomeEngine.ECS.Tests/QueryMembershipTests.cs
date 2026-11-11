using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class QueryMembershipTests
{
    [Fact]
    public void CreatingMatchingEntityAdvancesMembershipVersion()
    {
        using var world = new World();
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = query.MembershipVersion;

        Entity entity = world.Create(new Position { X = 1 });

        Assert.NotEqual(before, query.MembershipVersion);
        Assert.Equal([entity], Members(world, query));
    }

    [Fact]
    public void EnableFiltersTrackEntitiesEnteringAndLeavingMembership()
    {
        using var world = new World();
        Entity entity = world.Create(new VisibilityState());
        Query enabled = world.Query(new QueryDescription().With<VisibilityState>());
        Query disabled = world.Query(new QueryDescription().WithDisabled<VisibilityState>());
        ulong enabledBefore = enabled.MembershipVersion;
        ulong disabledBefore = disabled.MembershipVersion;

        world.Disable<VisibilityState>(entity);

        Assert.NotEqual(enabledBefore, enabled.MembershipVersion);
        Assert.NotEqual(disabledBefore, disabled.MembershipVersion);
        Assert.Empty(Members(world, enabled));
        Assert.Equal([entity], Members(world, disabled));
        enabledBefore = enabled.MembershipVersion;
        disabledBefore = disabled.MembershipVersion;

        world.Enable<VisibilityState>(entity);

        Assert.NotEqual(enabledBefore, enabled.MembershipVersion);
        Assert.NotEqual(disabledBefore, disabled.MembershipVersion);
        Assert.Equal([entity], Members(world, enabled));
        Assert.Empty(Members(world, disabled));
    }

    [Fact]
    public void SuccessfulValuePlaybackRetainsMembershipVersion()
    {
        using var world = new World();
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        Entity first = world.Create(new Position { X = 1 });
        _ = query.MembershipVersion;
        Entity second = world.Create(new Position { X = 2 });
        ulong before = query.MembershipVersion;

        using var commands = new CommandBuffer(world);
        commands.Set(first, new Position { X = 3 });
        commands.Playback();

        Assert.Equal(before, query.MembershipVersion);
        Assert.Equal([first, second], Members(world, query));
        Assert.Equal(3, world.Read<Position>(first).X);
    }

    [Fact]
    public void ExplicitCandidateFailurePreservesSourceMembershipVersionAndOrder()
    {
        using var world = new World();
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        Entity first = world.Create(new Position());
        Entity second = world.Create(new Position());
        Entity third = world.Create(new Position());
        ulong before = query.MembershipVersion;
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        Query candidateQuery = candidate.Query(new QueryDescription().WithStored<Position>());
        using var commands = new CommandBuffer(candidate);
        commands.Destroy(first);
        commands.Add(Entity.Null, new MembershipFault());

        Assert.Throws<ArgumentException>(commands.Playback);

        Assert.NotEqual(before, candidateQuery.MembershipVersion);
        Assert.Equal(before, query.MembershipVersion);
        Assert.Equal([first, second, third], Members(world, query));
    }

    [Fact]
    public void DestroyingMatchingEntityAdvancesMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new Position());
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = query.MembershipVersion;

        world.Destroy(entity);

        Assert.NotEqual(before, query.MembershipVersion);
        Assert.Empty(Members(world, query));
    }

    [Fact]
    public void AddingRequiredComponentAdvancesMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.Create();
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = query.MembershipVersion;

        world.Add(entity, new Position());

        Assert.NotEqual(before, query.MembershipVersion);
        Assert.Equal([entity], Members(world, query));
    }

    [Fact]
    public void RemovingRequiredComponentAdvancesMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new Position());
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = query.MembershipVersion;

        world.Remove<Position>(entity);

        Assert.NotEqual(before, query.MembershipVersion);
        Assert.Empty(Members(world, query));
    }

    [Fact]
    public void ComponentValueWritesRetainMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1 });
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = query.MembershipVersion;

        world.Set(entity, new Position { X = 2 });
        Assert.Equal(before, query.MembershipVersion);

        {
            using QueryScope rows = (query).Open();
            foreach (QueryChunk rowChunk in rows)
                for (int row = 0; row < rowChunk.Count; row++)
                rowChunk.Write<Position>(row).X = 3;

        };
        Assert.Equal(before, query.MembershipVersion);
        Assert.Equal([entity], Members(world, query));
        Assert.Equal(3, world.Read<Position>(entity).X);
    }

    [Fact]
    public void DestroyingFilteredEntityPreservesTheSelectedMembersAndVersion()
    {
        using var world = new World();
        Entity hidden = world.Create(new VisibilityState());
        Entity first = world.Create(new VisibilityState());
        Entity second = world.Create(new VisibilityState());
        Entity third = world.Create(new VisibilityState());
        world.Disable<VisibilityState>(hidden);
        Query query = world.Query(new QueryDescription().With<VisibilityState>());
        ulong before = query.MembershipVersion;
        Assert.Equal([first, second, third], Members(world, query).OrderBy(entity => entity.Id));

        world.Destroy(hidden);

        Assert.Equal(before, query.MembershipVersion);
        Assert.Equal([first, second, third], Members(world, query).OrderBy(entity => entity.Id));
    }

    [Fact]
    public void UnrelatedStructureAndEnableChangesRetainMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new Position());
        world.Add(entity, new VisibilityState());
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = query.MembershipVersion;

        Entity unrelated = world.Create(new Health());
        world.Add(unrelated, new Velocity());
        world.Remove<Velocity>(unrelated);
        world.Destroy(unrelated);
        world.Disable<VisibilityState>(entity);

        Assert.Equal(before, query.MembershipVersion);
        Assert.Equal([entity], Members(world, query));
    }

    [Fact]
    public void RepeatedQueryAcquisitionsShareMembershipVersion()
    {
        using var world = new World();
        Query first = world.Query(new QueryDescription().WithStored<Position>());
        Query second = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = first.MembershipVersion;
        Assert.Equal(before, second.MembershipVersion);

        Query retained = first;
        first = default;
        Entity entity = world.Create(new Position());

        Assert.NotEqual(before, second.MembershipVersion);
        Assert.Equal(second.MembershipVersion, retained.MembershipVersion);
        Assert.Equal([entity], Members(world, second));
    }

    [Fact]
    public void ReplacingLastEntityBetweenReadsAdvancesMembershipVersion()
    {
        using var world = new World();
        Entity first = world.Create(new Position());
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = query.MembershipVersion;

        world.Destroy(first);
        Entity replacement = world.Create(new Position());

        Assert.NotEqual(before, query.MembershipVersion);
        Assert.Equal([replacement], Members(world, query));
    }

    [Fact]
    public void QueryVersionWindowRetainsStructuralMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1 });
        ulong last = world.AcquireSystemVersion();
        Query query = world.Query(
            new QueryDescription().WithStored<Position>().WithChanged<Position>(0));
        ulong before = query.MembershipVersion;
        var entities = new List<Entity>();
        {
            using QueryScope rows = (query).Since(last).OpenRead();
            foreach (QueryChunk rowChunk in rows)
                for (int row = 0; row < rowChunk.Count; row++)
                entities.Add(rowChunk.Entities[row]);

        };
        Assert.Empty(entities);

        world.Set(entity, new Position { X = 2 });

        Assert.Equal(before, query.MembershipVersion);
        {
            using QueryScope rows = (query).Since(last).OpenRead();
            foreach (QueryChunk rowChunk in rows)
                for (int row = 0; row < rowChunk.Count; row++)
                entities.Add(rowChunk.Entities[row]);

        };
        Assert.Equal([entity], entities);
    }



    private static Entity[] Members(World world, Query query)
    {
        var entities = new List<Entity>();
        {
            using QueryScope rows = (query).OpenRead();
            foreach (QueryChunk rowChunk in rows)
                for (int row = 0; row < rowChunk.Count; row++)
                entities.Add(rowChunk.Entities[row]);

        };
        return entities.ToArray();
    }

    private struct MembershipFault : IAddedComponent;

}
