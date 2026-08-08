using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

internal sealed class QueryState
{
    private readonly List<QueryArchetypeMatch> _matches = new();
    private readonly List<Archetype> _archetypes = new();
    private Dictionary<(int WriteComponentId, int ReadComponentId), ReadWriteMatch[]>? _pairMatches;
    private ReadWriteMatch[]? _lastPairMatches;
    private int _lastWriteComponent;
    private int _lastReadComponent;

    private QueryState(QueryDefinition definition)
    {
        Definition = definition;
    }

    public QueryDefinition Definition { get; }

    public ReadOnlySpan<QueryArchetypeMatch> Matches => CollectionsMarshal.AsSpan(_matches);

    public ReadOnlySpan<Archetype> Archetypes => CollectionsMarshal.AsSpan(_archetypes);

    internal ReadOnlySpan<QueryArchetypeMatch> MatchList => CollectionsMarshal.AsSpan(_matches);

    internal static QueryState Create(QueryDefinition definition, ReadOnlySpan<Archetype> archetypes)
    {
        var state = new QueryState(definition);
        for (int i = 0; i < archetypes.Length; i++)
            state.TryAddArchetype(archetypes[i]);
        return state;
    }

    /// <summary>
    /// Clones the already-compiled query image by remapping its matched archetypes. This avoids
    /// re-evaluating every query definition against every archetype while a structural candidate
    /// is prepared; derivable read/write pair caches deliberately remain cold in the clone.
    /// </summary>
    internal QueryState CloneExact(DetachedTableMap tableMap)
    {
        ArgumentNullException.ThrowIfNull(tableMap);

        var clone = new QueryState(Definition);
        clone._matches.Capacity = _matches.Capacity;
        clone._archetypes.Capacity = _archetypes.Capacity;
        for (int i = 0; i < _matches.Count; i++)
        {
            QueryArchetypeMatch match = _matches[i].CloneFor(tableMap.Remap(_matches[i].Archetype));
            clone._matches.Add(match);
            clone._archetypes.Add(match.Archetype);
        }

        return clone;
    }

    internal bool TryAddArchetype(Archetype archetype)
    {
        if (!TryCreateMatch(Definition, archetype, out var match))
            return false;

        _matches.Add(match);
        _archetypes.Add(archetype);
        _pairMatches?.Clear();
        _lastPairMatches = null;
        return true;
    }

    internal ReadOnlySpan<ReadWriteMatch> AccessMatches<TWrite, TRead>(
        int writeComponentId,
        int readComponentId)
    {
        if (_lastPairMatches is not null &&
            _lastWriteComponent == writeComponentId &&
            _lastReadComponent == readComponentId)
        {
            return _lastPairMatches;
        }

        _pairMatches ??= new Dictionary<(int WriteComponentId, int ReadComponentId), ReadWriteMatch[]>();
        var key = (writeComponentId, readComponentId);
        if (_pairMatches.TryGetValue(key, out var existing))
        {
            CacheMatches(writeComponentId, readComponentId, existing);
            return existing;
        }

        var matches = CreateReadWriteMatches<TWrite, TRead>(
            this,
            writeComponentId,
            readComponentId);
        _pairMatches.Add(key, matches);
        CacheMatches(writeComponentId, readComponentId, matches);
        return matches;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CacheMatches(
        int writeComponentId,
        int readComponentId,
        ReadWriteMatch[] matches)
    {
        _lastWriteComponent = writeComponentId;
        _lastReadComponent = readComponentId;
        _lastPairMatches = matches;
    }

    private static ReadWriteMatch[] CreateReadWriteMatches<TWrite, TRead>(
        QueryState plan,
        int writeComponentId,
        int readComponentId)
    {
        ReadOnlySpan<QueryArchetypeMatch> source = plan.MatchList;
        var matches = new ReadWriteMatch[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            QueryArchetypeMatch match = source[i];
            if (!match.TryGetAccess(writeComponentId, out var writeAccess) ||
                !writeAccess.Access.CanRead() ||
                !writeAccess.Access.CanWrite())
            {
                throw new InvalidOperationException(
                    $"{typeof(TWrite).Name} was not declared for query read-write access.");
            }

            if (!match.TryGetAccess(readComponentId, out var readAccess) ||
                !readAccess.Access.CanRead())
            {
                throw new InvalidOperationException(
                    $"{typeof(TRead).Name} was not declared for query read access.");
            }

            matches[i] = new ReadWriteMatch(
                match.Archetype,
                writeAccess.ColumnIndex,
                readAccess.ColumnIndex,
                match.HasChangedFilter,
                match);
        }

        return matches;
    }

    internal bool TryGetMatch(Archetype archetype, out QueryArchetypeMatch match)
    {
        for (int i = 0; i < _matches.Count; i++)
        {
            if (ReferenceEquals(_matches[i].Archetype, archetype))
            {
                match = _matches[i];
                return true;
            }
        }

        match = null!;
        return false;
    }

    private static bool TryCreateMatch(
        QueryDefinition spec,
        Archetype archetype,
        out QueryArchetypeMatch match)
    {
        var builder = new QueryMatchBuilder(archetype);
        ReadOnlySpan<QueryTerm> terms = spec.Terms;
        for (int i = 0; i < terms.Length; i++)
        {
            if (!builder.TryAdd(terms[i]))
            {
                match = null!;
                return false;
            }
        }

        return builder.TryCreate(out match);
    }

    private struct QueryMatchBuilder
    {
        private readonly Archetype _archetype;
        private bool _hasAnyTerm;
        private bool _matchedAny;
        private List<ChangeTerm>? _exactTerms;
        private List<int>? _chunkColumns;
        private List<int>? _enabledMasks;
        private List<int>? _disabledMasks;
        private List<QueryColumnAccess>? _accessColumns;

        public QueryMatchBuilder(Archetype archetype)
        {
            _archetype = archetype;
        }

        public bool TryAdd(QueryTerm term)
        {
            var state = MatchTerm(term.Kind, _archetype.HasComponent(term.ComponentId));
            if (state == TermMatchState.Reject)
                return false;

            return state == TermMatchState.Skip ||
                   (TryAddChangeFilters(term) &&
                    TryAddMaskFilters(term) &&
                    TryAddAccess(term));
        }

        public bool TryCreate(out QueryArchetypeMatch match)
        {
            if (_hasAnyTerm && !_matchedAny)
            {
                match = null!;
                return false;
            }

            match = new QueryArchetypeMatch(
                _archetype,
                _exactTerms is null ? Array.Empty<ChangeTerm>() : _exactTerms.ToArray(),
                _chunkColumns is null ? Array.Empty<int>() : _chunkColumns.ToArray(),
                _enabledMasks is null ? Array.Empty<int>() : _enabledMasks.ToArray(),
                _disabledMasks is null ? Array.Empty<int>() : _disabledMasks.ToArray(),
                _accessColumns is null ? Array.Empty<QueryColumnAccess>() : _accessColumns.ToArray());
            return true;
        }

        private TermMatchState MatchTerm(QueryTermKind kind, bool present)
        {
            switch (kind)
            {
                case QueryTermKind.All:
                    return present ? TermMatchState.Include : TermMatchState.Reject;
                case QueryTermKind.None:
                    return present ? TermMatchState.Reject : TermMatchState.Skip;
                case QueryTermKind.Any:
                    _hasAnyTerm = true;
                    _matchedAny |= present;
                    return present ? TermMatchState.Include : TermMatchState.Skip;
                case QueryTermKind.Optional:
                    return present ? TermMatchState.Include : TermMatchState.Skip;
                default:
                    return TermMatchState.Reject;
            }
        }

        private bool TryAddChangeFilters(QueryTerm term)
        {
            if (!TryAddExactTerm(term, QueryTermFilter.Added))
                return false;

            if (!TryAddExactTerm(term, QueryTermFilter.Changed))
                return false;

            if ((term.Filters & QueryTermFilter.ChunkChanged) == 0)
                return true;

            if (!_archetype.TryColumn(term.ComponentId, out int chunkColumn))
                return false;

            (_chunkColumns ??= new List<int>()).Add(chunkColumn);
            return true;
        }

        private bool TryAddExactTerm(QueryTerm term, QueryTermFilter filter)
        {
            if ((term.Filters & filter) == 0)
                return true;

            if (!_archetype.TryColumn(term.ComponentId, out int column))
                return false;

            (_exactTerms ??= new List<ChangeTerm>()).Add(new ChangeTerm(column, filter));
            return true;
        }

        private bool TryAddMaskFilters(QueryTerm term)
        {
            if (!TryAddMask(term, QueryTermFilter.Enabled, ref _enabledMasks))
                return false;

            return TryAddMask(term, QueryTermFilter.Disabled, ref _disabledMasks);
        }

        private bool TryAddMask(QueryTerm term, QueryTermFilter filter, ref List<int>? masks)
        {
            if ((term.Filters & filter) == 0)
                return true;

            if (!_archetype.TryMask(term.ComponentId, out int mask))
                return false;

            (masks ??= new List<int>()).Add(mask);
            return true;
        }

        private bool TryAddAccess(QueryTerm term)
        {
            if (term.Access == QueryAccess.None)
                return true;

            if (!_archetype.TryColumn(term.ComponentId, out int accessColumn))
                return false;

            (_accessColumns ??= new List<QueryColumnAccess>()).Add(
                new QueryColumnAccess(term.ComponentId, accessColumn, term.Access));
            return true;
        }
    }

    private enum TermMatchState
    {
        Reject,
        Skip,
        Include,
    }
}

internal readonly struct ReadWriteMatch
{
    internal readonly Archetype Archetype;
    internal readonly int WriteColumn;
    internal readonly int ReadColumn;
    internal readonly bool HasChangedFilter;
    internal readonly QueryArchetypeMatch Match;

    internal ReadWriteMatch(
        Archetype archetype,
        int writeColumn,
        int readColumn,
        bool hasChangedFilter,
        QueryArchetypeMatch match)
    {
        Archetype = archetype;
        WriteColumn = writeColumn;
        ReadColumn = readColumn;
        HasChangedFilter = hasChangedFilter;
        Match = match;
    }
}

public sealed class QueryArchetypeMatch
{
    private readonly ChangeTerm[] _exactTerms;
    private readonly int[] _chunkColumns;
    private readonly int[] _enabledMasks;
    private readonly int[] _disabledMasks;
    private readonly QueryColumnAccess[] _accessColumns;

    internal QueryArchetypeMatch(
        Archetype archetype,
        ChangeTerm[] ownedExactTerms,
        int[] ownedChunkColumns,
        int[] ownedEnabledMasks,
        int[] ownedDisabledMasks,
        QueryColumnAccess[] ownedAccessColumns)
    {
        Archetype = archetype;
        _exactTerms = ownedExactTerms;
        _chunkColumns = ownedChunkColumns;
        _enabledMasks = ownedEnabledMasks;
        _disabledMasks = ownedDisabledMasks;
        _accessColumns = ownedAccessColumns;
    }

    public Archetype Archetype { get; }

    internal ReadOnlySpan<ChangeTerm> ExactTerms => _exactTerms;

    internal ReadOnlySpan<int> ChunkColumns => _chunkColumns;

    internal ReadOnlySpan<int> EnabledMasks => _enabledMasks;

    internal ReadOnlySpan<int> DisabledMasks => _disabledMasks;

    internal ReadOnlySpan<QueryColumnAccess> AccessColumns => _accessColumns;

    public bool HasChangedFilter => HasChangeFilter;

    public bool HasChangeFilter =>
        ExactTerms.Length > 0 ||
        ChunkColumns.Length > 0;

    public bool HasRowFilter =>
        ExactTerms.Length > 0 ||
        EnabledMasks.Length > 0 ||
        DisabledMasks.Length > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetAccess(int componentId, out QueryColumnAccess access)
    {
        for (int i = 0; i < AccessColumns.Length; i++)
        {
            if (AccessColumns[i].ComponentId == componentId)
            {
                access = AccessColumns[i];
                return true;
            }
        }

        access = default;
        return false;
    }

    internal QueryAccess GetDeclaredAccess(int componentId)
    {
        return TryGetAccess(componentId, out var access)
            ? access.Access
            : QueryAccess.None;
    }

    internal QueryArchetypeMatch CloneFor(Archetype archetype) =>
        new(
            archetype,
            CloneOwned(ExactTerms),
            CloneOwned(ChunkColumns),
            CloneOwned(EnabledMasks),
            CloneOwned(DisabledMasks),
            CloneOwned(AccessColumns));

    private static T[] CloneOwned<T>(ReadOnlySpan<T> source)
    {
        var clone = new T[source.Length];
        source.CopyTo(clone);
        return clone;
    }

    internal bool MatchesRow(Chunk chunk, int row, uint lastVersion)
    {
        for (int i = 0; i < EnabledMasks.Length; i++)
        {
            if (!chunk.IsEnabled(EnabledMasks[i], row))
                return false;
        }

        for (int i = 0; i < DisabledMasks.Length; i++)
        {
            if (chunk.IsEnabled(DisabledMasks[i], row))
                return false;
        }

        if (!MatchesExact(chunk, row, lastVersion))
            return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool MatchesChanged(Chunk chunk, uint lastSystemVersion)
    {
        return MatchesChunk(chunk, lastSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool MatchesChunkFilter(Chunk chunk, uint lastSystemVersion)
    {
        if (!MatchesChunkOnly(chunk, lastSystemVersion))
            return false;

        for (int i = 0; i < ExactTerms.Length; i++)
        {
            if (!VersionClock.IsNewer(chunk.ChangeVersions[ExactTerms[i].Column], lastSystemVersion))
                return false;
        }

        if (EnabledMasks.Length == 0 && DisabledMasks.Length == 0)
            return chunk.Count > 0;

        return RowFilterMask(chunk) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool MatchesChunk(Chunk chunk, uint lastSystemVersion)
    {
        if (!MatchesChunkFilter(chunk, lastSystemVersion))
            return false;

        if (!HasRowFilter)
            return true;

        if (ExactTerms.Length == 0)
            return true;

        return AnyRow(chunk, lastSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool MatchesChunkOnly(Chunk chunk, uint lastSystemVersion)
    {
        for (int i = 0; i < ChunkColumns.Length; i++)
        {
            if (!VersionClock.IsNewer(chunk.ChangeVersions[ChunkColumns[i]], lastSystemVersion))
                return false;
        }

        return true;
    }

    private bool MatchesExact(Chunk chunk, int row, uint lastVersion)
    {
        for (int i = 0; i < ExactTerms.Length; i++)
        {
            if (!MatchesTerm(chunk, ExactTerms[i], row, lastVersion))
                return false;
        }

        return true;
    }

    private bool AnyRow(Chunk chunk, uint lastVersion)
    {
        UInt128 rows = RowFilterMask(chunk);
        for (int row = 0; row < chunk.Count; row++)
        {
            UInt128 rowBit = (UInt128)1 << row;
            if ((rows & rowBit) != 0 && MatchesExact(chunk, row, lastVersion))
                return true;
        }

        return false;
    }

    private UInt128 RowFilterMask(Chunk chunk)
    {
        UInt128 rows = LiveRowMask(chunk);
        if (rows == 0)
            return 0;

        ReadOnlySpan<UInt128> masks = chunk.EnableMasks;
        for (int i = 0; i < EnabledMasks.Length; i++)
        {
            rows &= masks[EnabledMasks[i]];
            if (rows == 0)
                return 0;
        }

        for (int i = 0; i < DisabledMasks.Length; i++)
        {
            rows &= ~masks[DisabledMasks[i]];
            if (rows == 0)
                return 0;
        }

        return rows;
    }

    private static UInt128 LiveRowMask(Chunk chunk)
    {
        if (chunk.Count <= 0)
            return 0;

        return chunk.Count >= 128
            ? UInt128.MaxValue
            : ((UInt128)1 << chunk.Count) - 1;
    }

    private static bool MatchesTerm(Chunk chunk, ChangeTerm term, int row, uint lastVersion)
    {
        var version = term.Filter == QueryTermFilter.Added
            ? chunk.AddVersionRows(term.Column)[row]
            : chunk.WriteVersionRows(term.Column)[row];
        return VersionClock.IsNewer(version, lastVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int SharedSlot(int componentId) =>
        Archetype.SharedComponentIds.BinarySearch(componentId);

}

internal readonly struct ChangeTerm
{
    public ChangeTerm(int column, QueryTermFilter filter)
    {
        Column = column;
        Filter = filter;
    }

    public int Column { get; }

    public QueryTermFilter Filter { get; }
}

internal readonly struct QueryColumnAccess
{
    public QueryColumnAccess(int componentId, int columnIndex, QueryAccess access)
    {
        ComponentId = componentId;
        ColumnIndex = columnIndex;
        Access = access;
    }

    public int ComponentId { get; }

    public int ColumnIndex { get; }

    public QueryAccess Access { get; }
}

