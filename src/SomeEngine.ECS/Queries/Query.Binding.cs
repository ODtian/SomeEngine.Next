using System.Runtime.CompilerServices;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS;

// Predicates and matches are shared; each Query value carries its own observation version.
internal sealed class QueryDefinition(int id, QueryDescription description)
{
    internal readonly int Id = id;
    internal readonly QueryDescription Description = description;
    internal readonly int[] AccessTypes = CollectAccessTypes(description);

    private static int[] CollectAccessTypes(QueryDescription description)
    {
        var types = new List<int>();
        for (int type = 0; type < ComponentTypes.Count; type++)
            if (description.Required.Has(type) || description.OptionalReads.Has(type) || description.Any.Has(type))
                types.Add(type);
        return types.ToArray();
    }
}

internal static class QueryDefinitions
{
    private static readonly Dictionary<QueryDescription, QueryDefinition> Definitions = new(new DescriptionComparer());

    internal static QueryDefinition Get(QueryDescription description)
    {
        Array.Sort(description.Shared, static (left, right) => left.Type.CompareTo(right.Type));
        Array.Sort(description.Groups, static (left, right) => left.Type.CompareTo(right.Type));
        lock (Definitions)
        {
            if (Definitions.TryGetValue(description, out var found)) return found;
            QueryDescription copy = description.Copy();
            copy.Since = 0;
            var definition = new QueryDefinition(Definitions.Count, copy);
            Definitions.Add(copy, definition);
            return definition;
        }
    }

    private sealed class DescriptionComparer : IEqualityComparer<QueryDescription>
    {
        public bool Equals(QueryDescription? left, QueryDescription? right)
            => ReferenceEquals(left, right) || left is not null && right is not null
                && left.Required.Equals(right.Required) && left.Stored.Equals(right.Stored) && left.Excluded.Equals(right.Excluded)
                && left.Any.Equals(right.Any) && left.OptionalReads.Equals(right.OptionalReads)
                && left.Disabled.Equals(right.Disabled) && left.Changed.Equals(right.Changed)
                && left.Added.Equals(right.Added) && left.Shared.AsSpan().SequenceEqual(right.Shared)
                && left.Groups.AsSpan().SequenceEqual(right.Groups);

        public int GetHashCode(QueryDescription description)
        {
            var hash = new HashCode();
            description.Required.AddHash(ref hash); description.Stored.AddHash(ref hash); description.Excluded.AddHash(ref hash);
            description.Any.AddHash(ref hash); description.OptionalReads.AddHash(ref hash);
            description.Disabled.AddHash(ref hash); description.Changed.AddHash(ref hash);
            description.Added.AddHash(ref hash);
            foreach (var shared in description.Shared) { hash.Add(shared.Type); hash.Add(shared.Value); }
            foreach (var group in description.Groups) { hash.Add(group.Type); hash.Add(group.Value); }
            return hash.ToHashCode();
        }
    }
}

internal abstract class QueryCoverage
{
    internal PodColumnBuffer? FirstValues, SecondValues, ThirdValues;
    internal nint ValueTypes;
    internal bool AllRows;
}

internal sealed class QueryData : QueryCoverage
{
    internal EventLog<MembershipChange>? MembershipLog;
    internal ulong MembershipVersion;
    internal bool TracksMembership;
    internal readonly QueryDescription Description;
    internal readonly bool CountShared;
    internal QueryMatch[] Matches = new QueryMatch[4];
    internal int Count;
    internal readonly int SingleValueType = -1;
    internal PodColumnBuffer? SingleValues;
    internal Chunk? SingleChunk;
    internal int SingleChunkType = -1;
    internal ComponentBits UnmatchedColumns;
    private int _checked, _version;
    private int _checkedMatches = -1;

    internal QueryData(QueryDescription description)
    {
        Description = description;
        if (description.Required.Count == 1 && description.Excluded.Count == 0 && description.Any.Count == 0
            && description.Disabled.Count == 0 && description.Changed.Count == 0 && description.Added.Count == 0
            && description.Shared.Length == 0 && description.Groups.Length == 0)
            SingleValueType = description.Required.First;
        CountShared = description.Shared.Length == 1 && description.Required.Count == 1
            && !description.Stored.Has(description.Shared[0].Type)
            && description.Excluded.Count == 0 && description.Disabled.Count == 0 && description.Changed.Count == 0
            && description.Groups.Length == 0 && description.Any.Count == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RefreshIfNeeded(World world)
    {
        if (Volatile.Read(ref _checkedMatches) != world.QueryMatchVersion) Refresh(world);
    }

    internal void Refresh(World world)
    {
        lock (this)
        {
            if (_version != world.QueryVersion)
            {
                _checked = Count = 0;
                UnmatchedColumns = default;
                _version = world.QueryVersion;
            }
            for (int i = _checked; i < world.ArchetypeCount; i++)
            {
                Archetype archetype = world.Archetypes[i];
                if (archetype.Types.Bits.Contains(Description.Required)
                    && !archetype.Types.Bits.IntersectsExcept(Description.Excluded, archetype.Optional)
                    && Description.MatchesGroups(archetype)
                    && (Description.Any.Count == 0 || archetype.Types.Bits.Intersects(Description.Any)))
                {
                    if (Count == Matches.Length) Array.Resize(ref Matches, Math.Max(4, Count * 2));
                    Matches[Count++] = new(archetype, QueryRowFilter.Create(archetype, Description));
                }
                else foreach (ComponentType type in archetype.Components) UnmatchedColumns.Add(type.Id);
            }
            _checked = world.ArchetypeCount;
            bool allRows = Count == _checked;
            for (int i = 0; allRows && i < Count; i++) allRows = Matches[i].Filter is null;
            AllRows = allRows;
            if (!allRows) ValueTypes = 0;
            if (SingleChunk is not null)
                SingleChunk = Count == 1 && Matches[0].Filter is null && Matches[0].Archetype.ChunkCount == 1
                    ? Matches[0].Archetype.PrimaryChunk : null;
            Volatile.Write(ref _checkedMatches, world.QueryMatchVersion);
        }
    }

    internal void Release()
    {
        Matches = [];
        Count = _checked = 0;
        AllRows = false;
        SingleValues = null;
        SingleChunk = null;
        SingleChunkType = -1;
        FirstValues = SecondValues = ThirdValues = null;
        ValueTypes = 0;
        _checkedMatches = -1;
        UnmatchedColumns = default;
    }
}

internal static class QueryTypes<T> where T : struct
{
    internal static readonly QueryDescription Description = new QueryDescription().With<T>();
}

internal static class QueryTypes<T1, T2> where T1 : struct where T2 : struct
{
    internal static readonly QueryDescription Description = new QueryDescription().With<T1>().With<T2>();
}

internal static class QueryTypes<T1, T2, T3> where T1 : struct where T2 : struct where T3 : struct
{
    internal static readonly QueryDescription Description = new QueryDescription().With<T1>().With<T2>().With<T3>();
}

public sealed partial class World
{
    private QueryData?[] _queryBindings = [];
    private object? _queryBindingsLock;

    internal void RefreshQueryMatches()
    {
        _membershipCacheVersion++;
        QueryMatchVersion++;
        foreach (QueryData? data in _queryBindings)
            if (data is not null && (data.ValueTypes != 0 || data.SingleChunk is not null)) data.Refresh(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private QueryData BindQuery(QueryDefinition definition)
    {
        QueryData?[] bindings = Volatile.Read(ref _queryBindings);
        int id = definition.Id;
        return (uint)id < (uint)bindings.Length && Volatile.Read(ref bindings[id]) is { } data
            ? data : AddQueryBinding(definition);
    }

    private QueryData AddQueryBinding(QueryDefinition definition)
    {
        lock (LazyInitializer.EnsureInitialized(ref _queryBindingsLock))
        {
            int id = definition.Id;
            QueryData?[] bindings = _queryBindings;
            if ((uint)id < (uint)bindings.Length && bindings[id] is { } found) return found;
            if (id >= bindings.Length) Array.Resize(ref bindings, Math.Max(id + 1, Math.Max(16, bindings.Length * 2)));
            var data = new QueryData(definition.Description);
            Volatile.Write(ref bindings[id], data);
            Volatile.Write(ref _queryBindings, bindings);
            return data;
        }
    }

    private void ReleaseQueryBindings()
    {
        foreach (QueryData? data in _queryBindings) data?.Release();
        _queryBindings = [];
    }

    public Query Query<T>() where T : struct => Query(QueryTypes<T>.Description);
    public Query Query<T1, T2>() where T1 : struct where T2 : struct => Query(QueryTypes<T1, T2>.Description);
    public Query Query<T1, T2, T3>() where T1 : struct where T2 : struct where T3 : struct
        => Query(QueryTypes<T1, T2, T3>.Description);
}
