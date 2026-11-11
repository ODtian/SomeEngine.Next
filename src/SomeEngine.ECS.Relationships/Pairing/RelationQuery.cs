using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

public struct RelationQuery<TEnumerable, TRow> : IRelationEnumerable<TRow>
    where TEnumerable : struct, IQueryRelationEnumerable<TRow>
{
    private TEnumerable _enumerable;
    public RelationQuery(TEnumerable enumerable) => _enumerable = enumerable;
    public readonly TEnumerable Enumerable => _enumerable;

    public bool Enumerate<TOutput>(ref TOutput output)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct => EnumerateQuery(ref output, null);
    public bool Enumerate<TOutput>(ulong lastVersion, ref TOutput output)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct => EnumerateQuery(ref output, lastVersion);

    private bool EnumerateQuery<TOutput>(ref TOutput output, ulong? lastVersion)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct
    {
        var identities = new QueryIdentityOutput<TOutput, TRow>(output);
        try
        {
            World world = _enumerable.World;
            int count = _enumerable.QueryCount;
            ParticipantQueries inputs = default;
            var reads = new QueryDescription();
            if (count == 0) reads = _enumerable.AddReads(reads, 0);
            for (int position = 0; position < count; position++)
            {
                Query query = _enumerable.GetQuery(position);
                inputs[position] = lastVersion is { } version ? query.Since(version) : query;
                foreach (int type in query.Description.Definition.AccessTypes) reads.OptionalReads.Add(type);
                reads = _enumerable.AddReads(reads, position);
            }
            using QueryScope access = world.Query(reads).OpenRead();
            return EnumerateInputs(inputs, count, 0, new RelationQueryViews(world, access, default, default), ref identities);
        }
        finally { output = identities.Value; }
    }

    private bool EnumerateInputs<TOutput>(ParticipantQueries inputs, int count, int position,
        scoped RelationQueryViews views, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        if (position == count) return _enumerable.Enumerate(views, ref output);
        using QueryScope members = inputs[position].OpenRead();
        return EnumerateInputs(inputs, count, position + 1, views.WithQuery(position, members), ref output);
    }

    public int Count() => CountRows(null);
    public int Count(ulong lastVersion) => CountRows(lastVersion);
    private int CountRows(ulong? lastVersion)
    {
        var output = new RelationCount<TRow>();
        EnumerateQuery(ref output, lastVersion);
        return output.Count;
    }
    public bool Any() => HasRows(null);
    public bool Any(ulong lastVersion) => HasRows(lastVersion);
    private bool HasRows(ulong? lastVersion)
    {
        var output = new RelationAny<TRow>();
        EnumerateQuery(ref output, lastVersion);
        return output.Found;
    }
}

[InlineArray(8)]
internal struct ParticipantQueries { private Query _first; }

internal struct RelationCount<TRow> : IRelationOutput<TRow>
{
    internal int Count;
    public bool Add(in TRow row) { Count++; return true; }
}
internal struct RelationAny<TRow> : IRelationOutput<TRow>
{
    internal bool Found;
    public bool Add(in TRow row) { Found = true; return false; }
}
