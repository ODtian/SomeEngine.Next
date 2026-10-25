using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Queries;

public sealed class QueryDefinition
{
    private readonly QueryTerm[] _terms;
    private readonly QueryAccessEntry[] _accesses;
    private readonly WorldJobStorageAccess[] _jobStorageAccesses;
    private readonly WorldJobStorageAccess[] _membershipStorageAccesses;
    private readonly QueryDataAccess[] _dataAccesses;

    private QueryDefinition(QueryTerm[] ownedTerms)
    {
        ArgumentNullException.ThrowIfNull(ownedTerms);
        _terms = ownedTerms.Length == 0
            ? Array.Empty<QueryTerm>()
            : ownedTerms;
        Key = new QueryKey(_terms);

        var accesses = new List<QueryAccessEntry>();
        for (int i = 0; i < _terms.Length; i++)
        {
            QueryTerm term = _terms[i];
            if (term.Access != QueryAccess.None)
                accesses.Add(new QueryAccessEntry(term.ComponentId, term.Access, term.Kind));
        }

        _accesses = accesses.Count == 0
            ? Array.Empty<QueryAccessEntry>()
            : accesses.ToArray();
        _dataAccesses = CompileDataAccess(_terms);
        var jobStorage = new List<WorldJobStorageAccess>(_dataAccesses.Length);
        foreach (QueryDataAccess access in _dataAccesses)
        {
            CanWrite |= access.Access.CanWrite();
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(access.ComponentId);
            HasRelationshipWrite |= access.Access.CanWrite() &&
                (info.IsRelationshipSource || info.IsRelationshipTarget);
            if (access.Access != QueryAccess.None || access.Filters != QueryTermFilter.None)
                jobStorage.Add(new WorldJobStorageAccess(access.Storage, access.ComponentId,
                    access.Access.CanWrite() ? WorldStorageAccess.Write : WorldStorageAccess.Read));
        }
        _jobStorageAccesses = jobStorage.ToArray();
        var membershipStorage = new List<WorldJobStorageAccess>();
        foreach (QueryDataAccess access in _dataAccesses)
            if ((access.Filters & (QueryTermFilter.Enabled | QueryTermFilter.Disabled)) != 0)
                membershipStorage.Add(new WorldJobStorageAccess(
                    access.Storage, access.ComponentId, WorldStorageAccess.Read));
        _membershipStorageAccesses = membershipStorage.ToArray();
    }

    public static QueryDefinition Empty { get; } = new(Array.Empty<QueryTerm>());

    public ReadOnlySpan<QueryTerm> Terms => _terms;

    public ReadOnlySpan<QueryAccessEntry> Accesses => _accesses;

    public QueryKey Key { get; }

    /// <summary>Reports whether All or the sole Any term requires this component for every member.</summary>
    public bool Requires<T>() where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        int anyCount = 0;
        bool matchingAny = false;
        foreach (QueryTerm term in _terms)
        {
            if (term.Kind == QueryTermKind.All && term.ComponentId == componentId)
                return true;
            if (term.Kind == QueryTermKind.Any)
            {
                anyCount++;
                matchingAny |= term.ComponentId == componentId;
            }
        }
        return matchingAny && anyCount == 1;
    }

    /// <summary>Creates the optional storage-access union of both definitions.</summary>
    public QueryDefinition WithAccessFrom(QueryDefinition definition)
    {
        var terms = new List<QueryTerm>(_terms.Length + definition._terms.Length);
        AddAccessTerms(_terms, terms);
        AddAccessTerms(definition._terms, terms);
        return CreateNormalized(CollectionsMarshal.AsSpan(terms));
    }

    /// <summary>Preserves this query's membership and adds the input definition's optional storage access.</summary>
    public QueryDefinition WithAccess(QueryDefinition definition)
    {
        var terms = new List<QueryTerm>(definition._terms.Length);
        AddAccessTerms(definition._terms, terms);
        return CreateNormalized(_terms, CollectionsMarshal.AsSpan(terms));
    }

    private static void AddAccessTerms(ReadOnlySpan<QueryTerm> accesses, List<QueryTerm> terms)
    {
        foreach (QueryTerm access in accesses)
        {
            QueryAccess mode = access.Filters == QueryTermFilter.None
                ? access.Access
                : QueryAccessExtensions.Merge(access.Access, QueryAccess.Read);
            if (mode != QueryAccess.None)
                terms.Add(new QueryTerm(access.ComponentId, QueryTermKind.Optional, mode));
        }
    }

    /// <summary>Adds component read access while retaining this query's membership conditions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueryDefinition WithOptionalRead<T>() where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        foreach (QueryDataAccess access in _dataAccesses)
            if (access.ComponentId == componentId && access.Access.CanRead())
                return this;
        return AddOptionalRead<T>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private QueryDefinition AddOptionalRead<T>() where T : struct
    {
        QueryableTypeInfo info = QueryableTypeInfo.For<T>();
        info.Validate(QueryTermKind.Optional, QueryAccess.Read, QueryTermFilter.None);
        foreach (QueryTerm term in _terms)
            if (term.ComponentId == info.ComponentId && term.Kind == QueryTermKind.None)
                return this;
        QueryTerm[] terms = (QueryTerm[])_terms.Clone();
        bool found = false;
        for (int i = 0; i < terms.Length; i++)
        {
            QueryTerm term = terms[i];
            if (term.ComponentId != info.ComponentId)
                continue;
            if (term.Kind == QueryTermKind.None)
                return this;
            terms[i] = new QueryTerm(term.ComponentId, term.Kind,
                QueryAccessExtensions.Merge(term.Access, QueryAccess.Read), term.Filters);
            found = true;
        }

        if (found)
            return CreateNormalized(terms);
        return CreateNormalized(_terms,
            [new QueryTerm(info.ComponentId, QueryTermKind.Optional, QueryAccess.Read)]);
    }

    internal ReadOnlyMemory<WorldJobStorageAccess> JobStorageAccesses =>
        _jobStorageAccesses;

    internal ReadOnlyMemory<WorldJobStorageAccess> MembershipStorageAccesses =>
        _membershipStorageAccesses;

    internal ReadOnlySpan<QueryDataAccess> DataAccesses => _dataAccesses;

    internal bool HasRelationshipWrite { get; }

    public bool CanWrite { get; }

    /// <summary>
    /// Normalizes query terms once for every construction path. Keeping merge, conflict, and
    /// ordering rules here prevents generated-filter composition from drifting away from the
    /// public builder.
    /// </summary>
    internal static QueryDefinition CreateNormalized(ReadOnlySpan<QueryTerm> terms) =>
        CreateNormalized(terms, ReadOnlySpan<QueryTerm>.Empty);

    internal static QueryDefinition Combine(
        QueryDefinition direct,
        QueryDefinition filter)
    {
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(filter);
        return CreateNormalized(direct.Terms, filter.Terms);
    }

    private static QueryDefinition CreateNormalized(
        ReadOnlySpan<QueryTerm> first,
        ReadOnlySpan<QueryTerm> second)
    {
        if (first.Length == 0 && second.Length == 0)
            return Empty;

        var states = new Dictionary<(int ComponentId, QueryTermKind Kind), TermState>();
        AddTerms(first, states);
        AddTerms(second, states);

        var componentKinds = new Dictionary<int, QueryTermKind>();
        foreach (var pair in states)
        {
            int componentId = pair.Key.ComponentId;
            QueryTermKind kind = pair.Key.Kind;
            if (componentKinds.TryGetValue(componentId, out QueryTermKind existing) &&
                ((existing == QueryTermKind.None && kind != QueryTermKind.None) ||
                 (kind == QueryTermKind.None && existing != QueryTermKind.None)))
            {
                throw new InvalidOperationException(
                    $"Component ID {componentId} cannot be both excluded and included in the same query.");
            }
            componentKinds[componentId] = kind;
        }

        var normalized = new List<QueryTerm>(states.Count);
        foreach (var pair in states)
        {
            QueryTermFilter filters = pair.Value.Filters;
            if ((filters & QueryTermFilter.Enabled) != 0 &&
                (filters & QueryTermFilter.Disabled) != 0)
            {
                throw new InvalidOperationException(
                    $"Component ID {pair.Key.ComponentId} cannot be both Enabled and Disabled in one query.");
            }

            var term = new QueryTerm(
                pair.Key.ComponentId,
                pair.Key.Kind,
                pair.Value.Access,
                filters);
            QueryableTypeInfo.ForComponentId(term.ComponentId)
                .Validate(term.Kind, term.Access, term.Filters);
            normalized.Add(term);
        }

        normalized.Sort(static (left, right) =>
        {
            int kind = left.Kind.CompareTo(right.Kind);
            if (kind != 0)
                return kind;
            int component = left.ComponentId.CompareTo(right.ComponentId);
            if (component != 0)
                return component;
            int access = left.Access.CompareTo(right.Access);
            return access != 0 ? access : left.Filters.CompareTo(right.Filters);
        });
        return new QueryDefinition(normalized.ToArray());
    }

    private static void AddTerms(
        ReadOnlySpan<QueryTerm> terms,
        Dictionary<(int ComponentId, QueryTermKind Kind), TermState> states)
    {
        for (int i = 0; i < terms.Length; i++)
        {
            QueryTerm term = terms[i];
            var key = (term.ComponentId, term.Kind);
            if (states.TryGetValue(key, out TermState state))
            {
                state.Access = QueryAccessExtensions.Merge(state.Access, term.Access);
                state.Filters |= term.Filters;
                states[key] = state;
            }
            else
            {
                states.Add(key, new TermState(term.Access, term.Filters));
            }
        }
    }

    private struct TermState
    {
        internal TermState(QueryAccess access, QueryTermFilter filters)
        {
            Access = access;
            Filters = filters;
        }

        internal QueryAccess Access;
        internal QueryTermFilter Filters;
    }

    private static QueryDataAccess[] CompileDataAccess(ReadOnlySpan<QueryTerm> terms)
    {
        var data = new Dictionary<(WorldStorageKind Storage, int ComponentId), QueryDataAccess>();
        foreach (QueryTerm term in terms)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(term.ComponentId);
            int componentId = term.ComponentId;
            WorldStorageKind storage;
            if (BufferRegistry.TryGetHeaderComponentId(componentId, out int headerId))
            {
                componentId = headerId;
                storage = WorldStorageKind.Buffer;
            }
            else
            {
                if (info.Storage == StoragePath.Tag)
                    continue;
                storage = info.Storage switch
                {
                    StoragePath.Table => WorldStorageKind.Table,
                    StoragePath.Sparse => WorldStorageKind.Sparse,
                    StoragePath.Shared => WorldStorageKind.Shared,
                    _ => throw new InvalidOperationException("The query storage kind is unsupported."),
                };
            }

            var key = (storage, componentId);
            if (data.TryGetValue(key, out QueryDataAccess existing))
                data[key] = existing with
                {
                    Access = QueryAccessExtensions.Merge(existing.Access, term.Access),
                    Filters = existing.Filters | term.Filters,
                };
            else
                data.Add(key, new QueryDataAccess(info.Type, storage, componentId, term.Access, term.Filters));
        }
        return data.Values.OrderBy(static access => access.Storage)
            .ThenBy(static access => access.ComponentId).ToArray();
    }
}

/// <summary>
/// One normalized meaning for access to a data family. Matching still uses query terms;
/// scheduling and generated access both consume these same merged modes and filters.
/// </summary>
internal readonly record struct QueryDataAccess(
    Type ValueType,
    WorldStorageKind Storage,
    int ComponentId,
    QueryAccess Access,
    QueryTermFilter Filters);
