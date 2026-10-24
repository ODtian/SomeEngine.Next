using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Checks;

internal static class QueryMembersChecks
{
    internal static void Check()
    {
        using var world = new World(execution: new WorldJobs(JobSystem.Default));
        QueryHandle enabled = world.Query(world.QueryDefinition().Read<Visibility>().Enabled<Visibility>());
        QueryHandle disabled = world.Query(world.QueryDefinition().Read<Visibility>().Disabled<Visibility>());
        Require((false, false), Members());

        Entity first = world.CreateEntity(new Visibility());
        Require((true, false), Members());
        world.Disable<Visibility>(first);
        Require((false, true), Members());
        world.Enable<Visibility>(first);
        Require((true, false), Members());
        world.DestroyEntity(first);
        Require((false, false), Members());
        world.CreateEntity(new Visibility());
        Require((true, false), Members());
        world.ReleaseQuery(enabled);
        world.ReleaseQuery(disabled);

        (bool Enabled, bool Disabled) Members() =>
            (world.HasQueryMembers(enabled), world.HasQueryMembers(disabled));
    }

    private static void Require((bool Enabled, bool Disabled) expected, (bool Enabled, bool Disabled) actual)
    {
        if (actual != expected) throw new InvalidOperationException("Query members must follow the current entity and enable state.");
    }

    private struct Visibility : IEnableableComponent;
}
