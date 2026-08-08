using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Core.ECS;

/// <summary>
/// Parallel parent-before-child WorldTransform propagation over proven-disjoint dirty subtrees.
/// </summary>
/// <remarks>
/// The caller supplies the hierarchy maintenance token whose inverse generation must be used.
/// Organization nodes without the LocalTransform/WorldTransform pair are identity pass-through
/// nodes and do not stop traversal.
/// </remarks>
public static class ParallelTransformPropagation<TDomain>
    where TDomain : IHierarchyDomain
{
    public static HierarchyPropagation Schedule(
        World world,
        ReadOnlySpan<Entity> dirtyCandidates,
        HierarchyMaintenanceDependency<TDomain> maintenance,
        int rootsPerPacket = 1,
        JobScheduleOptions jobOptions = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[2];
        accesses[0] = ComponentJobAccess<LocalTransform>.Read(world);
        accesses[1] = ComponentJobAccess<WorldTransform>.Write(world);
        var propagationJob = new TransformPropagationJob();
        return HierarchyPropagationAdapter<TDomain>.Schedule(
            world,
            dirtyCandidates,
            in propagationJob,
            maintenance,
            accesses,
            new HierarchyPropagationScheduleOptions(rootsPerPacket, jobOptions));
    }

    private readonly struct TransformPropagationJob : IHierarchyPropagationJob<TDomain>
    {
        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            if (!context.Has<LocalTransform>() || !context.Has<WorldTransform>())
                return;

            TransformQvvs local = context.Read<LocalTransform>().Value;
            TransformQvvs worldValue = local;
            Entity ancestor = context.Parent;
            while (ancestor != Entity.Null && context.IsAlive(ancestor))
            {
                if (context.Has<LocalTransform>(ancestor) &&
                    context.Has<WorldTransform>(ancestor))
                {
                    WorldTransform parentWorld = context.Read<WorldTransform>(ancestor);
                    worldValue = TransformQvvs.Combine(parentWorld.Qvvs, local);
                    break;
                }
                ancestor = context.GetParent(ancestor);
            }

            WorldTransform current = context.Read<WorldTransform>();
            if (TransformEquals(current.Qvvs, worldValue))
                return;

            current.Qvvs = worldValue;
            context.Write(in current);
        }
    }

    private static bool TransformEquals(in TransformQvvs left, in TransformQvvs right) =>
        left.Position == right.Position &&
        left.Rotation == right.Rotation &&
        left.Stretch == right.Stretch &&
        left.Scale == right.Scale;
}

/// <summary>Default hierarchy-domain facade for <see cref="ParallelTransformPropagation{TDomain}"/>.</summary>
public static class ParallelTransformPropagation
{
    public static HierarchyPropagation Schedule(
        World world,
        ReadOnlySpan<Entity> dirtyCandidates,
        HierarchyMaintenanceDependency<DefaultHierarchyDomain> maintenance,
        int rootsPerPacket = 1,
        JobScheduleOptions jobOptions = default) =>
        ParallelTransformPropagation<DefaultHierarchyDomain>.Schedule(
            world,
            dirtyCandidates,
            maintenance,
            rootsPerPacket,
            jobOptions);
}
