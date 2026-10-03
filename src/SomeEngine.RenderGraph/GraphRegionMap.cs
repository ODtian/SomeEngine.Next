namespace SomeEngine.RenderGraph;

public delegate void GraphRegionBuilder<TKey, TValue>(
    ref RenderGraphEdit edit, GraphRegionId region, TKey key, TValue value);

/// <summary>Business keys and values associated with child regions of one parent.</summary>
public sealed class GraphRegionMap<TKey, TValue> where TKey : notnull
{
    private readonly record struct RegionValue(GraphRegionId Region, TValue Value);
    private readonly ImmutableDictionary<TKey, RegionValue> _regions;
    private readonly IEqualityComparer<TValue> _valueComparer;

    public GraphRegionMap(GraphRegionId parent, IEqualityComparer<TValue>? values = null)
        : this(parent, ImmutableDictionary<TKey, RegionValue>.Empty,
            values ?? EqualityComparer<TValue>.Default)
    {
    }

    private GraphRegionMap(GraphRegionId parent, ImmutableDictionary<TKey, RegionValue> regions,
        IEqualityComparer<TValue> values)
    {
        Parent = parent;
        _regions = regions;
        _valueComparer = values;
    }

    public GraphRegionId Parent { get; }

    /// <summary>Returns a new map for the caller to keep after committing the edit.</summary>
    public GraphRegionMap<TKey, TValue> Reconcile(ref RenderGraphEdit edit,
        IEnumerable<KeyValuePair<TKey, TValue>> desired, GraphRegionBuilder<TKey, TValue> build)
    {
        DevelopmentCheck.Require(desired is not null, "Region reconciliation requires desired keys and values.");
        DevelopmentCheck.Require(build is not null, "Region reconciliation requires a build callback.");
        KeyValuePair<TKey, TValue>[] entries = desired.ToArray();
        var keys = new HashSet<TKey>(entries.Select(static pair => pair.Key));
        DevelopmentCheck.Require(keys.Count == entries.Length, "Desired region keys must be unique.");
        foreach (var pair in _regions)
            if (!keys.Contains(pair.Key)) edit.Remove(pair.Value.Region);
        var regions = ImmutableDictionary.CreateBuilder<TKey, RegionValue>();
        foreach (var pair in entries)
        {
            bool exists = _regions.TryGetValue(pair.Key, out RegionValue previous);
            if (exists && _valueComparer.Equals(previous.Value, pair.Value))
            {
                regions.Add(pair.Key, previous);
                continue;
            }
            if (exists) edit.Remove(previous.Region);
            GraphRegionId region = edit.AddRegion($"Region {pair.Key}", Parent);
            build(ref edit, region, pair.Key, pair.Value);
            regions.Add(pair.Key, new RegionValue(region, pair.Value));
        }
        return new GraphRegionMap<TKey, TValue>(Parent, regions.ToImmutable(), _valueComparer);
    }

    public bool TryGetRegion(TKey key, out GraphRegionId region)
    {
        bool found = _regions.TryGetValue(key, out RegionValue value);
        region = value.Region;
        return found;
    }
}
