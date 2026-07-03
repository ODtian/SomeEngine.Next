using SomeEngine.ECS;
using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

/// <summary>
/// Compatibility view over a World-owned Query v2 record.
/// </summary>
public sealed class QueryView
{
    private readonly World _world;

    internal QueryView(World world, QueryHandle handle, QueryDefinition definition)
    {
        _world = world;
        Handle = handle;
        Definition = definition;
    }

    public QueryHandle Handle { get; }

    public QueryDefinition Definition { get; }

    public IReadOnlyList<Archetype> Archetypes => _world.GetQueryState(Handle).Archetypes;

    public bool HasChangedFilter
    {
        get
        {
            var terms = Definition.TermsArray;
            for (int i = 0; i < terms.Length; i++)
            {
                if ((terms[i].Filters & (
                    QueryTermFilter.Added |
                    QueryTermFilter.Changed |
                    QueryTermFilter.ChunkChanged)) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool HasRowFilter
    {
        get
        {
            var terms = Definition.TermsArray;
            for (int i = 0; i < terms.Length; i++)
            {
                if ((terms[i].Filters & (
                    QueryTermFilter.Added |
                    QueryTermFilter.Changed |
                    QueryTermFilter.Enabled |
                    QueryTermFilter.Disabled)) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool Matches(Archetype archetype) =>
        TryGetMatch(archetype, out _);

    internal bool MatchesRow(Archetype archetype, Chunk chunk, int row, uint lastVersion)
    {
        if (!TryGetMatch(archetype, out var match))
            return false;

        return match.MatchesRow(chunk, row, lastVersion);
    }

    internal bool MatchesRow(Archetype archetype, Chunk chunk, int row) =>
        MatchesRow(archetype, chunk, row, 0);

    public bool MatchesChunkChanged(Archetype archetype, Chunk chunk, uint lastSystemTick)
    {
        if (!TryGetMatch(archetype, out var match))
            return false;

        return match.MatchesChunk(chunk, lastSystemTick);
    }

    public bool IsReadWrite(int componentId)
    {
        var terms = Definition.TermsArray;
        for (int i = 0; i < terms.Length; i++)
        {
            if (terms[i].ComponentId == componentId && terms[i].Access.CanWrite())
                return true;
        }

        return false;
    }

    internal QueryCursor CreateCursor(uint lastSystemTick, uint currentSystemTick) =>
        _world.RunQuery(Handle, lastSystemTick, currentSystemTick);

    internal QueryCursor RunAuto()
    {
        uint last = _world.AcquireSystemTick();
        return CreateCursor(last, _world.CurrentTick);
    }

    private bool TryGetMatch(Archetype archetype, out QueryArchetypeMatch match) =>
        _world.GetQueryState(Handle).TryGetMatch(archetype, out match);
}

