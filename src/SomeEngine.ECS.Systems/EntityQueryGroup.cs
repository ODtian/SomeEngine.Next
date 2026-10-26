namespace SomeEngine.ECS.Systems;

public interface IEntityQueryGroupJob
{
    /// <summary>Returns true after synchronous work completes, or false before writes to recapture changed work.</summary>
    bool Execute(scoped EntityQueryGroup group);
}

/// <summary>All work views borrowed under one component and reference resource grant.</summary>
public readonly ref struct EntityQueryGroup
{
    private readonly World _world;
    private readonly EntityQueryData[][] _queries;
    private readonly QueryReadResources? _resources;
    private readonly uint _version;
    internal EntityQueryGroup(World world, EntityQueryData[][] queries, QueryReadResources? resources, uint version)
    { _world = world; _queries = queries; _resources = resources; _version = version; }
    public int Count => _queries.Length;
    public EntityQueryViews Get(int index) => new(_queries[index]);

    /// <summary>Joins the selected work range before returning; work indices retain their original values.</summary>
    public void ExecuteParallel<TJob>(in TJob job, int start, int count) where TJob : struct, IEntityQueryJob =>
        EntityQueryJobs.ExecuteGroupQueries(_world, _queries, _resources, in job, start, count);

    /// <summary>Resolves derived work under the current covering accesses and joins its compatible ranges.</summary>
    public void ExecuteParallel<TWork, TJob>(in TWork work, in TJob job, int start, int count)
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryJob =>
        EntityQueryJobs.ExecuteGroupWork(_world, _resources, _version, in work, in job, start, count);
}
