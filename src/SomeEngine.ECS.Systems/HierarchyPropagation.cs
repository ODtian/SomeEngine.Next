using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public interface IHierarchyPropagationJob<TDomain> where TDomain : struct, IHierarchyDomain
{
    void Execute(ref HierarchyPropagationContext<TDomain> context);
}

internal readonly record struct ComponentRow(Chunk Chunk, int Type, int Row);

public readonly ref struct HierarchyPropagationContext<TDomain> where TDomain : struct, IHierarchyDomain
{
    private readonly World _world;
    private readonly WorldJobs _jobs;
    private readonly List<ComponentRow> _writes;
    private readonly Entity _root;
    private readonly ComponentBits _writtenTypes;
    public Entity Entity { get; }
    public Entity Parent => Hierarchy<TDomain>.GetParent(_world, Entity);
    internal HierarchyPropagationContext(World world, WorldJobs jobs, Entity root, Entity entity,
        List<ComponentRow> writes, ComponentBits writtenTypes)
        => (_world, _jobs, _root, Entity, _writes, _writtenTypes) = (world, jobs, root, entity, writes, writtenTypes);
    public bool Has<T>() where T : struct => _world.Has<T>(Entity);
    public bool Has<T>(Entity entity) where T : struct => _world.Has<T>(entity);
    public bool IsAlive(Entity entity) => _world.IsAlive(entity);
    public Entity GetParent(Entity entity) => Hierarchy<TDomain>.GetParent(_world, entity);
    public ref readonly T Read<T>() where T : struct => ref Read<T>(Entity);
    public ref readonly T Read<T>(Entity entity) where T : struct
    {
        RequireStorage<T>();
        ref EntityRecord record = ref RequireComponent<T>(entity);
        if (_writtenTypes.Has(ComponentType<T>.TypeId) && !ContainsReadEntity(entity))
            throw new InvalidOperationException("Read writable component values from this root or its ancestors.");
        _world.RequireValue<T>(false);
        if (ComponentType<T>.IsSharedType)
            return ref _world.ReadSharedColumn<T>(record.Chunk!.Columns[ComponentType<T>.TypeId], record.Row);
        return ref ColumnEntry.At<T>(ref record.Chunk!.Columns[ComponentType<T>.TypeId], record.Row);
    }

    private ref EntityRecord RequireComponent<T>(Entity entity) where T : struct
    {
        ref EntityRecord record = ref _world.Resolve(entity);
        int type = ComponentType<T>.TypeId;
        if ((uint)type >= (uint)record.Chunk!.Columns.Length || !record.Chunk.Columns[type].Has(record.Row))
            throw new InvalidOperationException("The entity requires the requested component value.");
        return ref record;
    }

    private bool ContainsReadEntity(Entity entity)
    {
        if (entity == Entity) return true;
        for (Entity current = entity; current != Entity.Null; current = Hierarchy<TDomain>.GetParent(_world, current))
            if (current == _root) return true;
        for (Entity current = Hierarchy<TDomain>.GetParent(_world, _root); current != Entity.Null;
            current = Hierarchy<TDomain>.GetParent(_world, current))
            if (current == entity) return true;
        return false;
    }
    private static void RequireStorage<T>() where T : struct
    {
        if (!ComponentType<T>.Value.IsJobStorageAliasFree)
            throw new InvalidOperationException("Hierarchy Jobs require independent component storage.");
    }
    public void Write<T>(in T value) where T : struct
    {
        RequireStorage<T>();
        ComponentType type = ComponentType<T>.Value;
        if (type.UsesStructureWrites) throw new InvalidOperationException("Record component layout changes in commands.");
        ref EntityRecord record = ref RequireComponent<T>(Entity);
        Chunk chunk = record.Chunk!;
        long first = ((long)chunk.Id << 32) + record.Row;
        _jobs.Scheduler.RequireCurrentAccess(JobResourceAccess.Write(_jobs.Resource(WorldValueKind.Table, type.Id), first, 1), false);
        if (type.TracksWrites)
            _jobs.Scheduler.RequireCurrentAccess(JobResourceAccess.Write(_jobs.Resource(WorldValueKind.Changes, type.Id), chunk.Id, 1), false);
        ColumnEntry.At<T>(ref chunk.Columns[type.Id], record.Row) = value;
        if (type.TracksWrites) _writes.Add(new(chunk, type.Id, record.Row));
    }
}

public static class HierarchyPropagation<TDomain> where TDomain : struct, IHierarchyDomain
{
    public static JobHandle Schedule<TAction>(World world, ReadOnlySpan<Entity> dirtyCandidates,
        in TAction action, SystemAccesses accesses, JobHandle dependency = default,
        JobScheduleOptions options = default, int rootsPerRange = 1)
        where TAction : struct, IHierarchyPropagationJob<TDomain>
    {
        if (rootsPerRange <= 0) throw new ArgumentOutOfRangeException(nameof(rootsPerRange));
        ComponentBits writtenTypes = default;
        foreach (WorldValueAccess access in accesses.Values)
            if (access.Kind is WorldValueKind.Table or WorldValueKind.Sparse)
            {
                ComponentType type = ComponentTypes.Get(access.Type);
                if (!type.IsJobStorageAliasFree)
                    throw new InvalidOperationException("Hierarchy Jobs require independent component storage.");
                if (access.Kind == WorldValueKind.Table && access.Write) writtenTypes.Add(access.Type);
            }
        var jobs = (WorldJobs)world.Execution!;
        var data = new PropagationData<TAction>(world, jobs, dirtyCandidates.ToArray(), action, rootsPerRange, writtenTypes);
        return jobs.Scheduler.Schedule(new Propagate<TAction>(data), jobs.CollectAccess(accesses), options: options, dependency: dependency);
    }

    private sealed class HierarchyRootChanges(Entity root)
    {
        internal readonly Entity Root = root;
        internal readonly List<ComponentRow> WrittenRows = [];
    }

    private sealed class PropagationData<TAction>(World world, WorldJobs jobs, Entity[] candidates, TAction action,
        int rootsPerRange, ComponentBits writtenTypes)
        where TAction : struct, IHierarchyPropagationJob<TDomain>
    {
        internal readonly World World = world;
        internal readonly WorldJobs Jobs = jobs;
        internal readonly Entity[] Candidates = candidates;
        internal readonly TAction Action = action;
        internal readonly int RootsPerRange = rootsPerRange;
        internal readonly ComponentBits WrittenTypes = writtenTypes;
    }

    private readonly struct Propagate<TAction>(PropagationData<TAction> data) : IJob
        where TAction : struct, IHierarchyPropagationJob<TDomain>
    {
        public void Execute()
        {
            World world = data.World;
            world.BeginRead();
            try
            {
                var dirty = new HashSet<Entity>();
                foreach (Entity entity in data.Candidates) if (world.IsAlive(entity)) dirty.Add(entity);
                var roots = new List<HierarchyRootChanges>();
                foreach (Entity entity in dirty)
                {
                    Entity parent = Hierarchy<TDomain>.GetParent(world, entity);
                    while (parent != Entity.Null && !dirty.Contains(parent)) parent = Hierarchy<TDomain>.GetParent(world, parent);
                    if (parent == Entity.Null) roots.Add(new(entity));
                }
                HierarchyRootChanges[] work = roots.ToArray();
                ulong version = 0;
                var ranges = new HierarchyRootRange[(work.Length + data.RootsPerRange - 1) / data.RootsPerRange];
                for (int index = 0; index < ranges.Length; index++)
                {
                    int first = index * data.RootsPerRange;
                    ranges[index] = new(work, first, Math.Min(data.RootsPerRange, work.Length - first));
                }
                try { data.Jobs.Scheduler.ExecuteRanges(ranges, ranges.Length, new PropagateRoot<TAction>(data)); }
                finally
                {
                    foreach (HierarchyRootChanges root in work)
                        foreach (ComponentRow write in root.WrittenRows)
                        {
                            if (version == 0) version = world.NextVersion();
                            ComponentType type = ComponentTypes.Get(write.Type);
                            write.Chunk.Changes![write.Chunk.Columns[write.Type].ColumnPlusOne - 1]
                                .Stamp(write.Row, version, false, type.TracksAdded);
                        }
                }
            }
            finally { world.EndRead(); }
        }
    }

    private readonly record struct HierarchyRootRange(HierarchyRootChanges[] Roots, int First, int Count);

    private readonly struct PropagateRoot<TAction>(PropagationData<TAction> data) : IRangeWork<HierarchyRootRange>
        where TAction : struct, IHierarchyPropagationJob<TDomain>
    {
        public void Execute(in HierarchyRootRange range)
        {
            for (int index = range.First; index < range.First + range.Count; index++) ExecuteRoot(range.Roots[index]);
        }
        private void ExecuteRoot(HierarchyRootChanges root)
        {
            TAction action = data.Action;
            var context = new HierarchyPropagationContext<TDomain>(data.World, data.Jobs, root.Root, root.Root, root.WrittenRows, data.WrittenTypes);
            action.Execute(ref context);
            foreach (Entity entity in Hierarchy<TDomain>.GetDescendants(data.World, root.Root))
            {
                context = new(data.World, data.Jobs, root.Root, entity, root.WrittenRows, data.WrittenTypes);
                action.Execute(ref context);
            }
        }
    }
}
