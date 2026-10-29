namespace SomeEngine.ECS.Systems;

public interface IEntityQueryGroupJob
{
    /// <summary>Returns true after synchronous work completes, or false before writes to recapture changed work.</summary>
    bool Execute(scoped EntityQueryGroup group);
}

/// <summary>All work views borrowed under one component and reference resource grant.</summary>
public readonly ref struct EntityQueryGroup : IDisposable
{
    private readonly World _world;
    private readonly EntityQueryData[][] _queries;
    private readonly QueryReadResources? _resources;
    private readonly uint _version;
    private readonly EntityQueryJobs.GroupQueries? _ranges;
    internal EntityQueryGroup(World world, EntityQueryData[][] queries, QueryReadResources? resources, uint version)
    { _world = world; _queries = queries; _resources = resources; _version = version; _ranges = null; }
    private EntityQueryGroup(World world, QueryReadResources? resources, uint version, EntityQueryJobs.GroupQueries ranges)
    { _world = world; _queries = []; _resources = resources; _version = version; _ranges = ranges; }
    public int Count => _ranges is null ? _queries.Length : _ranges.Count;
    public EntityQueryViews Get(int index) => _ranges is null ? new(_queries[index]) : _ranges.Get(index);

    /// <summary>Borrows derived query ranges within the current covering grant until Dispose.</summary>
    public EntityQueryGroup Borrow<TWork>(in TWork work) where TWork : struct, IEntityQueryWork =>
        new(_world, _resources, _version, EntityQueryJobs.GroupQueries.Create(_world, _resources, _version, in work));

    /// <summary>Refreshes this derived group's current entities and reference resources.</summary>
    public void Refresh<TWork>(in TWork work) where TWork : struct, IEntityQueryWork => _ranges!.Refresh(in work);

    public void Dispose() => _ranges?.Dispose();

    /// <summary>Joins the selected work range before returning; work indices retain their original values.</summary>
    public void ExecuteParallel<TJob>(in TJob job, int start, int count) where TJob : struct, IEntityQueryJob
    {
        if (_ranges is null) EntityQueryJobs.ExecuteGroupQueries(_world, _queries, _resources, in job, start, count);
        else _ranges.ExecuteParallel(in job, start, count);
    }

    /// <summary>Resolves derived work under the current covering accesses and joins its compatible ranges.</summary>
    public void ExecuteParallel<TWork, TJob>(in TWork work, in TJob job, int start, int count)
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryJob =>
        EntityQueryJobs.ExecuteGroupWork(_world, _resources, _version, in work, in job, start, count);
}
