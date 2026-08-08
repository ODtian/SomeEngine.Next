using SomeEngine.ECS.Hierarchy;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Schedules deferred Parent-to-Children reconciliation for one hierarchy domain.
/// </summary>
public static class HierarchyMaintenanceSystem<TDomain>
    where TDomain : IHierarchyDomain
{
    public static JobHandle Schedule(World world, JobHandle dependency = default)
    {
        return ScheduleDependency(world, dependency).Handle;
    }

    /// <summary>
    /// Schedules maintenance and returns a domain- and World-qualified dependency token for
    /// consumers that require a fresh inverse hierarchy generation.
    /// </summary>
    public static HierarchyMaintenanceDependency<TDomain> ScheduleDependency(
        World world,
        JobHandle dependency = default)
    {
        ArgumentNullException.ThrowIfNull(world);

        var evidence = new HierarchyMaintenanceEvidence();
        var job = new MaintenanceJob(world, evidence);
        // Validation failure restores canonical Parent preimages, so the exceptional path is a
        // real Parent writer even though successful maintenance only reads Parent.
        JobHandle handle = WorldStorageJobSchedule.ScheduleTopologyWrite(
            world,
            HierarchyJobAccess<TDomain>.ParentWrite(world),
            in job,
            dependency);
        return new HierarchyMaintenanceDependency<TDomain>(world, handle, evidence);
    }

    private readonly struct MaintenanceJob : IJob
    {
        private readonly World _world;
        private readonly HierarchyMaintenanceEvidence _evidence;

        internal MaintenanceJob(World world, HierarchyMaintenanceEvidence evidence)
        {
            _world = world;
            _evidence = evidence;
        }

        public void Execute()
        {
            HierarchyJobAccess<TDomain>.Maintain(_world);
            if (!_world.ActiveStructureRoot.Hierarchy.TryDomain<TDomain>(out var store))
            {
                throw new InvalidOperationException(
                    "Hierarchy maintenance did not materialize its domain store.");
            }
            _evidence.Publish(store.InverseRevision);
        }
    }
}

/// <summary>
/// Evidence that a handle publishes the inverse generation for one hierarchy domain and World.
/// </summary>
public readonly struct HierarchyMaintenanceDependency<TDomain>
    where TDomain : IHierarchyDomain
{
    private readonly World? _world;
    private readonly HierarchyMaintenanceEvidence? _evidence;

    internal HierarchyMaintenanceDependency(
        World world,
        JobHandle handle,
        HierarchyMaintenanceEvidence evidence)
    {
        _world = world;
        Handle = handle;
        _evidence = evidence;
    }

    public JobHandle Handle { get; }

    public bool IsValid => _world is not null && _evidence is not null;

    internal void RequireWorld(World world)
    {
        if (_world is null || _evidence is null)
        {
            throw new InvalidOperationException(
                "Hierarchy propagation requires an explicit maintenance dependency.");
        }
        if (!ReferenceEquals(_world, world))
        {
            throw new InvalidOperationException(
                "The hierarchy maintenance dependency belongs to a different World.");
        }
    }

    internal long RequireFresh(World world)
    {
        RequireWorld(world);
        long expected = _evidence!.RequireRevision();
        if (!world.ActiveStructureRoot.Hierarchy.TryDomain<TDomain>(out var store) ||
            !store.IsInverseFresh ||
            store.InverseRevision != expected)
        {
            throw new InvalidOperationException(
                "The hierarchy maintenance dependency is stale; canonical Parent changed or a newer inverse generation was published after it completed.");
        }
        return expected;
    }
}

internal sealed class HierarchyMaintenanceEvidence
{
    private long _revision;

    internal void Publish(long revision)
    {
        if (revision <= 0)
            throw new InvalidOperationException("Hierarchy inverse revision must be positive.");
        Volatile.Write(ref _revision, revision);
    }

    internal long RequireRevision()
    {
        long revision = Volatile.Read(ref _revision);
        return revision > 0
            ? revision
            : throw new InvalidOperationException(
                "Hierarchy maintenance did not publish freshness evidence.");
    }
}
