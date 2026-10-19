using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations.Bitmap;

/// <summary>A fixed ordered candidate list whose endpoints belong to the participating query.</summary>
public readonly struct FixedCandidatePairs : IQueryRelationEnumerable<(Entity, Entity)>
{
    private readonly QueryHandle _members;
    private readonly (Entity, Entity)[] _pairs;

    public FixedCandidatePairs(World world, QueryHandle members, (Entity, Entity)[] pairs)
    {
        World = world;
        _members = members;
        _pairs = pairs;
    }

    public World World { get; }
    public int QueryCount => 1;
    public static bool ReadsFromSingleQuery => true;
    public QueryHandle GetQuery(int position) => _members;
    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        (Entity, Entity)[] pairs = _pairs;
        foreach (var pair in pairs)
        {
            if (!output.Add(in pair))
                return false;
        }
        return true;
    }
}
