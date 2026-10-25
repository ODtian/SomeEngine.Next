using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>Selects the static resource description for an in/ref component parameter.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class JobResourcesAttribute<TResources> : Attribute
    where TResources : struct;

/// <summary>
/// Selects a descriptor by type. An open generic descriptor is closed by the source generator
/// with the component's type arguments, allowing generic jobs to keep typed static dispatch.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class JobResourcesAttribute(Type resourceType) : Attribute
{
    public Type ResourceType { get; } = resourceType;
}

/// <summary>
/// Describes the actual data reached through one component. Resource bindings are collected
/// after semantic dependencies complete. GetVersion changes when a referenced object's data
/// bindings change while the component value stays in place.
/// </summary>
public interface IJobComponentResources<TComponent>
    where TComponent : struct, IComponent
{
    static abstract void Collect(in TComponent component, ref JobResourceAccessBuilder resources);

    static virtual long GetVersion(in TComponent component) => 0;
}

/// <summary>Collects accesses to actual objects and array ranges for the current scheduler.</summary>
public readonly ref struct JobResourceAccessBuilder
{
    private readonly Scheduler _scheduler;
    private readonly List<ComponentResourceUse>? _accesses;
    private readonly List<object>? _references;
    private readonly List<JobResourceAccess>? _jobAccesses;

    /// <summary>Reuses component and space descriptions for an explicitly scheduled Job.</summary>
    public JobResourceAccessBuilder(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        _scheduler = scheduler;
        _jobAccesses = accesses;
    }

    internal JobResourceAccessBuilder(
        Scheduler scheduler,
        List<ComponentResourceUse> accesses,
        List<object> references)
    {
        _scheduler = scheduler;
        _accesses = accesses;
        _references = references;
    }

    public void Read(object data) =>
        Add(data, JobResourceAccess.Read(_scheduler.GetResourceToken(data)));

    public void Read(object data, long start, long length) =>
        Add(data, JobResourceAccess.Read(_scheduler.GetResourceToken(data), start, length));

    public void Read<T>(ReadOnlyMemory<T> data) =>
        Add(JobResourceAccess.GetArraySegment(data).Array!, JobResourceAccess.Read(_scheduler, data));

    public void Read<T>(Memory<T> data) => Read((ReadOnlyMemory<T>)data);

    public void Read<T>(ReadOnlyMemory<T> data, long start, long length) =>
        Add(JobResourceAccess.GetArraySegment(data).Array!, JobResourceAccess.Read(_scheduler, data, start, length));

    public void Read<T>(Memory<T> data, long start, long length) =>
        Read((ReadOnlyMemory<T>)data, start, length);

    public void Write(object data) =>
        Add(data, JobResourceAccess.Write(_scheduler.GetResourceToken(data)));

    public void Write(object data, long start, long length) =>
        Add(data, JobResourceAccess.Write(_scheduler.GetResourceToken(data), start, length));

    public void Write<T>(Memory<T> data) =>
        Add(JobResourceAccess.GetArraySegment<T>(data).Array!, JobResourceAccess.Write(_scheduler, data));

    public void Write<T>(Memory<T> data, long start, long length) =>
        Add(JobResourceAccess.GetArraySegment<T>(data).Array!, JobResourceAccess.Write(_scheduler, data, start, length));

    public void Exclusive(object data) =>
        Add(data, JobResourceAccess.Exclusive(_scheduler.GetResourceToken(data)));

    public void Exclusive(object data, long start, long length) =>
        Add(data, JobResourceAccess.Exclusive(_scheduler.GetResourceToken(data), start, length));

    public void Exclusive<T>(Memory<T> data) =>
        Add(JobResourceAccess.GetArraySegment<T>(data).Array!, JobResourceAccess.Exclusive(_scheduler, data));

    public void Exclusive<T>(Memory<T> data, long start, long length) =>
        Add(JobResourceAccess.GetArraySegment<T>(data).Array!, JobResourceAccess.Exclusive(_scheduler, data, start, length));

    private void Add(object data, JobResourceAccess access)
    {
        if (_jobAccesses is { } jobs)
            jobs.Add(access);
        else
            _accesses!.Add(new ComponentResourceUse(data, access));
    }

    internal void Retain<T>(in T component) where T : struct => _references!.Add(component);
}

internal readonly record struct ComponentResourceUse(object Data, JobResourceAccess Access);

internal interface IComponentResourceBinding
{
    long GetVersion(ref JobEntityRow row);
    void Collect(ref JobEntityRow row, ref JobResourceAccessBuilder resources);
}

internal sealed class ComponentResourceBinding<TComponent, TResources> : IComponentResourceBinding
    where TComponent : struct, IComponent
    where TResources : struct, IJobComponentResources<TComponent>
{
    public long GetVersion(ref JobEntityRow row) => TResources.GetVersion(in row.Read<TComponent>());

    public void Collect(ref JobEntityRow row, ref JobResourceAccessBuilder resources)
    {
        ref readonly TComponent component = ref row.Read<TComponent>();
        resources.Retain(in component);
        TResources.Collect(in component, ref resources);
    }
}
