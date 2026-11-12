using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs
{
    internal override void RegisterEventAccess(JobResourceAccess access)
    {
        if (JobThreads.IsActive) { _scheduler.RequireCurrentAccess(access, true); return; }
        if (s_current is not { } current || !ReferenceEquals(current.Jobs, this)) return;
        AccessBuilder<JobResourceAccess> accesses = new();
        try { accesses.Add(access); _scheduler.AppendSynchronousAccess(current.Handle, accesses); }
        finally { accesses.Clear(); }
    }
    public JobResourceAccess[] CollectAccess(SystemAccesses additional, bool structureWrite = false)
    {
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            AddStructureAccess(structureWrite, ref accesses);
            foreach (WorldValueAccess value in additional.Values)
            {
                if (!structureWrite && value.Kind is (WorldValueKind.Table or WorldValueKind.Sparse))
                    RequireJobStorage(ComponentTypes.Get(value.Type));
                AddValue(ref accesses, value);
            }
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); }
    }
    /// <summary>Declares structure and the reads required by the World's event producers.</summary>
    public override JobResourceAccess[] StructureRead => CollectStructureAccess(false);

    /// <summary>Declares structure edits and their event writes.</summary>
    public override JobResourceAccess[] StructureWrite => CollectStructureAccess(true);

    /// <summary>Declares direct World.Read component access together with StructureRead.</summary>
    public JobResourceAccess Read<T>() where T : struct
    {
        CheckActive();
        ComponentType component = ComponentType<T>.Value;
        RequireJobStorage(component);
        return JobResourceAccess.Read(component.UsesStructureWrites
            ? _structure : Resource(WorldValueKind.Table, component.Id));
    }

    /// <summary>Collects the structure and component permissions consumed by Query.OpenRead.</summary>
    public override JobResourceAccess[] CollectReadAccess(Query query)
    {
        QueryDescription description = query.Description;
        QueryDefinition definition = description.Definition;
        foreach (int type in definition.AccessTypes) RequireJobStorage(ComponentTypes.Get(type));
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            AddStructureAccess(false, ref accesses);
            BuildQueryValues(description, definition, [], false, ref accesses);
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); }
    }

    public JobResourceAccess[] CollectReadAccess<TComponent, TResources>(Query query)
        where TComponent : struct where TResources : struct, IJobComponentResources<TComponent>
    {
        QueryDescription description = query.Description;
        QueryDefinition definition = description.Definition;
        foreach (int type in definition.AccessTypes)
            if (type != ComponentType<TComponent>.TypeId) RequireJobStorage(ComponentTypes.Get(type));
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            AddStructureAccess(false, ref accesses);
            BuildQueryValues(description, definition, [], false, ref accesses);
            var resources = new JobResourceAccessBuilder(_scheduler, ref accesses);
            using QueryScope scope = query.OpenRead();
            foreach (QueryChunk chunk in scope)
                if (chunk.TryRead<TComponent>(out ReadOnlySpan<TComponent> components))
                    ComponentResources.CollectRange<TComponent, TResources>(components, ref resources);
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); }
    }

    private JobResourceAccess[] CollectStructureAccess(bool write)
    {
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            AddStructureAccess(write, ref accesses);
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); }
    }

    internal void AddStructureAccess(bool write, ref AccessBuilder<JobResourceAccess> accesses)
    {
        CheckActive();
        accesses.Add(write ? JobResourceAccess.Write(_structure) : JobResourceAccess.Read(_structure));
        _world.CollectEventAccesses(ref accesses, write);
    }

    private static JobResourceAccess[] CopyAccesses(in AccessBuilder<JobResourceAccess> accesses)
    {
        var result = new JobResourceAccess[accesses.Count];
        for (int index = 0; index < result.Length; index++) result[index] = accesses.Get(index);
        return result;
    }

    private static void RequireJobStorage(ComponentType component)
    {
        if (!component.IsJobStorageAliasFree)
            throw new InvalidOperationException("Component Job access requires values with independent storage.");
    }
}
