using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Tests;

public sealed class QueryMembershipTests
{
    [Fact]
    public void CreatingMatchingEntityAdvancesMembershipVersion()
    {
        using var world = new World();
        QueryHandle query = world.Query(world.QueryDefinition().All<Position>());
        uint before = world.MembershipVersion(query);

        Entity entity = world.CreateEntity(new Position { X = 1 });

        Assert.NotEqual(before, world.MembershipVersion(query));
        Assert.Equal([entity], Members(world, query));
    }

    [Fact]
    public void EnableFiltersTrackEntitiesEnteringAndLeavingMembership()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new VisibilityState());
        QueryHandle enabled = world.Query(world.QueryDefinition().Enabled<VisibilityState>());
        QueryHandle disabled = world.Query(world.QueryDefinition().Disabled<VisibilityState>());
        uint enabledBefore = world.MembershipVersion(enabled);
        uint disabledBefore = world.MembershipVersion(disabled);

        world.Disable<VisibilityState>(entity);

        Assert.NotEqual(enabledBefore, world.MembershipVersion(enabled));
        Assert.NotEqual(disabledBefore, world.MembershipVersion(disabled));
        Assert.Empty(Members(world, enabled));
        Assert.Equal([entity], Members(world, disabled));
        enabledBefore = world.MembershipVersion(enabled);
        disabledBefore = world.MembershipVersion(disabled);

        world.Enable<VisibilityState>(entity);

        Assert.NotEqual(enabledBefore, world.MembershipVersion(enabled));
        Assert.NotEqual(disabledBefore, world.MembershipVersion(disabled));
        Assert.Equal([entity], Members(world, enabled));
        Assert.Empty(Members(world, disabled));
    }

    [Fact]
    public void SuccessfulValuePlaybackRetainsMembershipVersion()
    {
        using var world = new World();
        QueryHandle query = world.Query(world.QueryDefinition().Read<Position>());
        Entity first = world.CreateEntity(new Position { X = 1 });
        _ = world.MembershipVersion(query);
        Entity second = world.CreateEntity(new Position { X = 2 });
        uint before = world.MembershipVersion(query);

        using var commands = new CommandBuffer(world);
        commands.Replace(first, new Position { X = 3 });
        commands.Playback();

        Assert.Equal(before, world.MembershipVersion(query));
        Assert.Equal([first, second], Members(world, query));
        Assert.Equal(3, world.Read<Position>(first).X);
    }

    [Fact]
    public void FailedStructuralPlaybackRestoresMembershipVersionAndOrder()
    {
        using var world = new World();
        QueryHandle query = world.Query(world.QueryDefinition().All<Position>());
        Entity first = world.CreateEntity(new Position());
        _ = world.MembershipVersion(query);
        Entity second = world.CreateEntity(new Position());
        Entity third = world.CreateEntity(new Position());
        uint before = world.MembershipVersion(query);
        uint candidateVersion = before;
        world.Hooks<MembershipFault>().OnAdd(
            (DeferredWorld _, Entity _, in MembershipFault _) =>
            {
                candidateVersion = world.MembershipVersion(query);
                throw new MembershipException();
            });

        using var commands = new CommandBuffer(world);
        commands.DestroyEntity(first);
        commands.Add(second, new MembershipFault());

        Assert.Throws<MembershipException>(commands.Playback);

        Assert.NotEqual(before, candidateVersion);
        Assert.Equal(before, world.MembershipVersion(query));
        Assert.Equal([first, second, third], Members(world, query));
    }

    [Fact]
    public void DestroyingMatchingEntityAdvancesMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Position());
        QueryHandle query = world.Query(world.QueryDefinition().All<Position>());
        uint before = world.MembershipVersion(query);

        world.DestroyEntity(entity);

        Assert.NotEqual(before, world.MembershipVersion(query));
        Assert.Empty(Members(world, query));
    }

    [Fact]
    public void AddingRequiredComponentAdvancesMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        QueryHandle query = world.Query(world.QueryDefinition().All<Position>());
        uint before = world.MembershipVersion(query);

        world.Add(entity, new Position());

        Assert.NotEqual(before, world.MembershipVersion(query));
        Assert.Equal([entity], Members(world, query));
    }

    [Fact]
    public void RemovingRequiredComponentAdvancesMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Position());
        QueryHandle query = world.Query(world.QueryDefinition().All<Position>());
        uint before = world.MembershipVersion(query);

        world.Remove<Position>(entity);

        Assert.NotEqual(before, world.MembershipVersion(query));
        Assert.Empty(Members(world, query));
    }

    [Fact]
    public void ComponentValueWritesRetainMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Position { X = 1 });
        QueryHandle query = world.Query(world.QueryDefinition().ReadWrite<Position>());
        uint before = world.MembershipVersion(query);

        world.Replace(entity, new Position { X = 2 });
        Assert.Equal(before, world.MembershipVersion(query));

        world.ExecuteQuery(query, rows =>
        {
            foreach (var row in rows.Rows)
                row.ReadWrite<Position>().X = 3;
        });
        Assert.Equal(before, world.MembershipVersion(query));
        Assert.Equal([entity], Members(world, query));
        Assert.Equal(3, world.Read<Position>(entity).X);
    }

    [Fact]
    public void SwapDeleteAdvancesVersionWhenMatchedEntitiesChangeOrder()
    {
        using var world = new World();
        Entity hidden = world.CreateEntity(new VisibilityState());
        Entity first = world.CreateEntity(new VisibilityState());
        Entity second = world.CreateEntity(new VisibilityState());
        Entity third = world.CreateEntity(new VisibilityState());
        world.Disable<VisibilityState>(hidden);
        QueryHandle query = world.Query(world.QueryDefinition().Enabled<VisibilityState>());
        uint before = world.MembershipVersion(query);
        Assert.Equal([first, second, third], Members(world, query));

        world.DestroyEntity(hidden);

        Assert.NotEqual(before, world.MembershipVersion(query));
        Assert.Equal([third, first, second], Members(world, query));
    }

    [Fact]
    public void UnrelatedStructureAndEnableChangesRetainMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Position());
        world.Add(entity, new VisibilityState());
        QueryHandle query = world.Query(world.QueryDefinition().All<Position>());
        uint before = world.MembershipVersion(query);

        Entity unrelated = world.CreateEntity(new Health());
        world.Add(unrelated, new Velocity());
        world.Remove<Velocity>(unrelated);
        world.DestroyEntity(unrelated);
        world.Disable<VisibilityState>(entity);

        Assert.Equal(before, world.MembershipVersion(query));
        Assert.Equal([entity], Members(world, query));
    }

    [Fact]
    public void RepeatedQueryAcquisitionsShareMembershipVersion()
    {
        using var world = new World();
        QueryHandle first = world.Query(world.QueryDefinition().All<Position>());
        QueryHandle second = world.Query(world.QueryDefinition().All<Position>());
        uint before = world.MembershipVersion(first);
        Assert.Equal(before, world.MembershipVersion(second));

        world.ReleaseQuery(first);
        Entity entity = world.CreateEntity(new Position());

        Assert.NotEqual(before, world.MembershipVersion(second));
        Assert.Equal(world.MembershipVersion(second), world.MembershipVersion(first));
        Assert.Equal([entity], Members(world, second));
    }

    [Fact]
    public void ReplacingLastEntityBetweenReadsAdvancesMembershipVersion()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Position());
        QueryHandle query = world.Query(world.QueryDefinition().All<Position>());
        uint before = world.MembershipVersion(query);

        world.DestroyEntity(first);
        Entity replacement = world.CreateEntity(new Position());

        Assert.NotEqual(before, world.MembershipVersion(query));
        Assert.Equal([replacement], Members(world, query));
    }

    [Fact]
    public void QueryVersionWindowRetainsStructuralMembershipVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Position { X = 1 });
        uint last = world.AcquireSystemTick();
        QueryHandle query = world.Query(
            world.QueryDefinition().Read<Position>().Changed<Position>());
        uint before = world.MembershipVersion(query);
        var entities = new List<Entity>();
        world.ExecuteQuery(query, last, rows =>
        {
            foreach (var row in rows.Rows)
                entities.Add(row.Entity);
        });
        Assert.Empty(entities);

        world.Replace(entity, new Position { X = 2 });

        Assert.Equal(before, world.MembershipVersion(query));
        world.ExecuteQuery(query, last, rows =>
        {
            foreach (var row in rows.Rows)
                entities.Add(row.Entity);
        });
        Assert.Equal([entity], entities);
    }

#if DEBUG
    [Fact]
    public void MembershipVersionReportsInvalidAndReleasedQueries()
    {
        using var world = new World();
        Assert.Throws<InvalidOperationException>(() => world.MembershipVersion(default));
        QueryHandle query = world.Query(world.QueryDefinition().All<Position>());
        world.ReleaseQuery(query);
        _ = world.Query(world.QueryDefinition().All<Health>());

        Assert.Throws<InvalidOperationException>(() => world.MembershipVersion(query));
    }
#endif

    private static Entity[] Members(World world, QueryHandle query)
    {
        var entities = new List<Entity>();
        world.ExecuteQuery(query, rows =>
        {
            foreach (var row in rows.Rows)
                entities.Add(row.Entity);
        });
        return entities.ToArray();
    }

    private struct MembershipFault : IComponent;

    private sealed class MembershipException : Exception;
}
