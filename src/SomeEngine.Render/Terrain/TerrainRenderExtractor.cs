using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

/// <summary>Captures immutable terrain requests from terrain references in the game world.</summary>
public sealed partial class TerrainRenderExtractor : IRenderExtractionSystem
{
    private readonly Dictionary<Entity, Resources.Terrain> _sources = [];
    private readonly Dictionary<Entity, TerrainDataRequest[]> _previous = [];
    private readonly Dictionary<Entity, TerrainDataRequest[]> _updates = [];
    private readonly HashSet<Entity> _collected = [];

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<TerrainSource>();
        query.Write<RenderTerrainRequests>();
    }

    public void Reset()
    {
        _collected.Clear();
        _updates.Clear();
    }

    private readonly TerrainSources _inputs = new();

    public void Collect(QueryChunkView chunk) => CollectInputs(chunk, true);

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) => CollectInputs(chunk, false);

    private bool CollectInputs(QueryChunkView chunk, bool fullCollection)
    {
        _inputs.Collect(ReadOnlyQueryPacketJobs.Borrow(chunk), chunk.LastSystemVersion, fullCollection);
        bool changed = MergeInputs(_inputs);
        _inputs.Reset();
        return changed;
    }

    public bool CompleteCollection(RenderExtractionContext context, bool fullCollection)
    {
        if (fullCollection)
            foreach (Entity source in context.ChangedSources)
                if (!_collected.Contains(source))
                {
                    _sources.Remove(source);
                    _previous.Remove(source);
                }

        var requests = new Dictionary<Resources.Terrain, TerrainDataRequest[]>();
        foreach ((Entity source, Resources.Terrain terrain) in _sources)
        {
            if (!requests.TryGetValue(terrain, out TerrainDataRequest[]? data))
                requests.Add(terrain, data = terrain.CaptureRequests());
            if (_collected.Contains(source) || !_previous.TryGetValue(source, out var previous)
                || !ReferenceEquals(previous, data))
                _updates[source] = data;
        }
        return _updates.Count != 0;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach (RenderMirror mirror in context.Mirrors)
            if (!_sources.ContainsKey(mirror.Source)) context.RemoveIfExists<RenderTerrainRequests>(mirror.RenderEntity);
        foreach ((Entity source, TerrainDataRequest[] data) in _updates)
        {
            context.Upsert(context.RetainMirror(source), new RenderTerrainRequests(data));
            _previous[source] = data;
        }
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach ((Entity source, TerrainDataRequest[] data) in _updates)
        {
            context.UpdateExisting(context.RequireMirror(source), new RenderTerrainRequests(data));
            _previous[source] = data;
        }
    }
}

internal readonly record struct RenderTerrainRequests(TerrainDataRequest[] Requests) : IComponent;
