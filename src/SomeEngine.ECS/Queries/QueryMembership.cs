using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Queries;

internal sealed class QueryMembership
{
    private readonly Lock _gate = new();
    private readonly List<Entity> _entities = new();
    private readonly List<ChunkMembers> _chunks = new();
    private uint _version;

    internal QueryMembership Clone()
    {
        lock (_gate)
        {
            var clone = new QueryMembership { _version = _version };
            clone._entities.AddRange(_entities);
            clone._chunks.AddRange(_chunks);
            return clone;
        }
    }

    internal uint ReadVersion(QueryState query)
    {
        lock (_gate)
        {
            if (RefreshChunks(query) && RefreshEntities(query))
                _version = unchecked(_version + 1);
            return _version;
        }
    }

    private bool RefreshChunks(QueryState query)
    {
        int index = 0;
        bool changed = false;
        foreach (QueryArchetypeMatch match in query.Matches)
        {
            foreach (var chunk in match.Archetype.Chunks)
            {
                UInt128 rows = match.RowFilterMask(chunk);
                if (rows == 0)
                    continue;
                changed |= Store(_chunks, index++, new ChunkMembers(
                    chunk.PersistentIdentity, chunk.OrderVersion, rows));
            }
        }
        return Trim(_chunks, index) || changed;
    }

    private bool RefreshEntities(QueryState query)
    {
        int index = 0;
        bool changed = false;
        foreach (QueryArchetypeMatch match in query.Matches)
        {
            foreach (var chunk in match.Archetype.Chunks)
            {
                UInt128 rows = match.RowFilterMask(chunk);
                for (int row = 0; row < chunk.Count; row++)
                {
                    if ((rows & ((UInt128)1 << row)) == 0)
                        continue;
                    changed |= Store(_entities, index++, chunk.Entities[row]);
                }
            }
        }

        return Trim(_entities, index) || changed;
    }

    private static bool Store<T>(List<T> values, int index, T value)
        where T : IEquatable<T>
    {
        if (index == values.Count)
        {
            values.Add(value);
            return true;
        }
        if (values[index].Equals(value))
            return false;
        values[index] = value;
        return true;
    }

    private static bool Trim<T>(List<T> values, int count)
    {
        if (values.Count == count)
            return false;
        values.RemoveRange(count, values.Count - count);
        return true;
    }

    private readonly record struct ChunkMembers(long ChunkId, uint OrderVersion, UInt128 Rows);
}
