using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>Writes one target through its current component root and the batch's shared version.</summary>
public interface IComponentWriteJob<TComponent> where TComponent : struct, IComponent
{
    void Execute(int index, ref TComponent component);
}

/// <summary>Joins target writes before the enclosing component-write callback returns.</summary>
public static class ComponentWriteJobs
{
    public static void ExecuteParallel<TComponent, TJob, TResources>(
        scoped ComponentWriteBatch<TComponent> roots, ReadOnlySpan<Entity> targets, in TJob job)
        where TComponent : struct, IComponent
        where TJob : struct, IComponentWriteJob<TComponent>
        where TResources : struct, IJobComponentResources<TComponent>
    {
        if (targets.IsEmpty) return;
        World world = roots.World;
        WorldStructureRoot structure = roots.Structure;
        if (!WorldJobs.TryGetScheduler(world, out Scheduler? scheduler))
        {
            TJob local = job;
            for (int index = 0; index < targets.Length; index++)
                local.Execute(index, ref roots.Write(targets[index]));
            return;
        }

        ComponentTargetGroups data = TargetGroupCache<TComponent, TJob, TResources>.Worlds.GetValue(world, static _ => new());
        bool targetsChanged = !targets.SequenceEqual(data.Targets);
        if (targetsChanged) data.Targets = targets.ToArray();
        do
        {
            Collect<TComponent, TResources>(structure, scheduler, roots.IsSparse, data.Targets,
                data.CurrentAccesses, data.CurrentStarts);
            if (targetsChanged || !data.Matches(data.CurrentAccesses, data.CurrentStarts))
            {
                data.Accesses.Clear();
                data.Accesses.AddRange(data.CurrentAccesses);
                data.Starts.Clear();
                data.Starts.AddRange(data.CurrentStarts);
                data.BuildGroups();
                targetsChanged = false;
            }
            if (data.Accesses.Count == 0)
            {
                scheduler.ExecuteParallel(new WriteGroups<TComponent, TJob>(structure, data, job,
                    roots.IsSparse, roots.CurrentSystemVersion), data.Groups.Length);
                return;
            }
            data.ReferencesChanged = false;
            var work = new BindReferencesAndWrite<TComponent, TJob, TResources>(structure, scheduler, data,
                job, roots.IsSparse, roots.CurrentSystemVersion);
            scheduler.ExecuteParallel(work, 1, 1, CollectionsMarshal.AsSpan(data.Accesses));
        }
        while (data.ReferencesChanged);
    }

    private static class TargetGroupCache<TComponent, TJob, TResources>
        where TComponent : struct, IComponent
        where TJob : struct, IComponentWriteJob<TComponent>
        where TResources : struct, IJobComponentResources<TComponent>
    {
        internal static readonly ConditionalWeakTable<World, ComponentTargetGroups> Worlds = new();
    }

    private sealed class ComponentTargetGroups
    {
        internal Entity[] Targets = [];
        internal readonly List<JobResourceAccess> Accesses = [];
        internal readonly List<int> Starts = [];
        internal readonly List<JobResourceAccess> CurrentAccesses = [];
        internal readonly List<int> CurrentStarts = [];
        internal int[][] Groups = [];
        internal bool ReferencesChanged;

        internal bool Matches(List<JobResourceAccess> accesses, List<int> starts)
        {
            return CollectionsMarshal.AsSpan(Starts).SequenceEqual(CollectionsMarshal.AsSpan(starts)) &&
                ComponentResourceGroups.AccessesMatch(CollectionsMarshal.AsSpan(Accesses), CollectionsMarshal.AsSpan(accesses));
        }

        internal void BuildGroups()
        {
            var groups = new ComponentResourceGroups(Targets.Length);
            var roots = new Dictionary<Entity, int>();
            var buckets = new Dictionary<ComponentResourceIdentity, List<ComponentResourceRange>>();
            for (int row = 0; row < Targets.Length; row++)
            {
                if (roots.TryGetValue(Targets[row], out int previous)) groups.Join(previous, row);
                else roots.Add(Targets[row], row);
                for (int access = Starts[row]; access < Starts[row + 1]; access++)
                    ComponentResourceGroups.AddAccess(Accesses[access], row, buckets);
            }
            foreach (List<ComponentResourceRange> ranges in buckets.Values) groups.JoinRanges(ranges);
            Groups = groups.Build();
        }
    }

    private static void Collect<TComponent, TResources>(WorldStructureRoot structure, Scheduler scheduler, bool sparse,
        Entity[] targets, List<JobResourceAccess> accesses, List<int> starts)
        where TComponent : struct, IComponent
        where TResources : struct, IJobComponentResources<TComponent>
    {
        accesses.Clear();
        starts.Clear();
        starts.Add(0);
        var builder = new JobResourceAccessBuilder(scheduler, accesses);
        foreach (Entity target in targets)
        {
            ref readonly TComponent component = ref (sparse
                ? ref structure.Sparse.ReadRef<TComponent>(target)
                : ref structure.Components.ReadRef<TComponent>(target));
            TResources.Collect(in component, ref builder);
            starts.Add(accesses.Count);
        }
    }

    private readonly struct BindReferencesAndWrite<TComponent, TJob, TResources>(WorldStructureRoot structure, Scheduler scheduler,
        ComponentTargetGroups data, TJob job, bool sparse, uint version) : IJobParallelFor
        where TComponent : struct, IComponent
        where TJob : struct, IComponentWriteJob<TComponent>
        where TResources : struct, IJobComponentResources<TComponent>
    {
        public void Execute(int index)
        {
            Collect<TComponent, TResources>(structure, scheduler, sparse, data.Targets,
                data.CurrentAccesses, data.CurrentStarts);
            if (!data.Matches(data.CurrentAccesses, data.CurrentStarts))
            {
                data.ReferencesChanged = true;
                return;
            }
            scheduler.ExecuteParallel(new WriteGroups<TComponent, TJob>(structure, data, job, sparse, version), data.Groups.Length);
        }
    }

    private readonly struct WriteGroups<TComponent, TJob>(WorldStructureRoot structure, ComponentTargetGroups data, TJob job,
        bool sparse, uint version) : IJobParallelFor
        where TComponent : struct, IComponent
        where TJob : struct, IComponentWriteJob<TComponent>
    {
        public void Execute(int index)
        {
            TJob local = job;
            foreach (int row in data.Groups[index])
            {
                ref TComponent component = ref (sparse
                    ? ref structure.Sparse.WriteRef<TComponent>(data.Targets[row], version)
                    : ref structure.Components.WriteRef<TComponent>(data.Targets[row], version));
                local.Execute(row, ref component);
            }
        }
    }
}
