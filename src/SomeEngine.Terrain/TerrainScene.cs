using System.Collections.Concurrent;
using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>Placement identities, spatial chunk entities and geometry build batches for a game world.</summary>
public sealed class TerrainScene
{
    private readonly ITerrainMesher? _mesher;
    private readonly ITerrainClusterAdapter? _adapter;
    private readonly Dictionary<Resources.Terrain, TerrainGeometryRequest> _gpuBuilds = [];
    internal TerrainClusterAdapter? GpuAdapter { get; }
    internal object RenderKey { get; } = new();
    private readonly Dictionary<int, PlacementData> _placements = [];
    private readonly Dictionary<ChunkKey, ChunkData> _chunks = [];
    private readonly Dictionary<GeometryKey, GeometryData> _geometryByKey = [];
    private readonly Dictionary<int, GeometryData> _geometryById = [];
    private readonly Dictionary<Resources.Terrain, MeshBatch> _batches = [];
    private readonly HashSet<int> _dirtyGeometry = [];
    private readonly HashSet<Resources.Terrain> _subscriptions = [];
    private readonly ConcurrentQueue<TerrainChange> _changes = new();
    private World? _world;
    private QueryHandle _chunkQuery;
    private int _nextPlacementId, _nextGeometryId;
    private bool _placementsChanged;

    public TerrainChunkGrid Grid { get; }

    public TerrainScene(ITerrainClusterAdapter adapter, TerrainChunkGrid grid, ITerrainMesher? mesher = null)
    {
        _adapter = adapter;
        _mesher = mesher ?? new MarchingCubesTerrainMesher();
        Grid = grid;
    }

    public TerrainScene(TerrainClusterAdapter adapter, TerrainChunkGrid grid)
    {
        GpuAdapter = adapter;
        Grid = grid;
    }

    /// <summary>The latest GPU batch, with separate device and rendering-world binding completion.</summary>
    public TerrainGpuBuild GetGpuBuild(Resources.Terrain terrain)
    {
        TerrainGeometryRequest build = _gpuBuilds[terrain];
        return new(build.Revision, build.Completion.Handle, build.Visibility.Handle);
    }

    public int Add(TerrainPlacement placement)
    {
        int id = ++_nextPlacementId;
        _placements.Add(id, new(placement));
        _placementsChanged = true;
        return id;
    }

    public void Remove(int placementId)
    {
        _placements.Remove(placementId);
        _placementsChanged = true;
    }

    /// <summary>Reads the currently committed game-world chunk versions.</summary>
    public TerrainChunk[] GetChunks(int placementId)
    {
        if (_world is null) return [];
        var result = new List<TerrainChunk>();
        _world.ExecuteQuery(_chunkQuery, query =>
        {
            foreach (QueryChunkView chunk in query.Chunks)
            foreach (TerrainChunk value in chunk.Read<TerrainChunk>())
                if (value.PlacementId == placementId) result.Add(value);
        });
        return result.ToArray();
    }

    public Mesh? GetGeometry(int geometryId) => _geometryById[geometryId].Mesh;

    /// <summary>Seeds matching CPU geometry before adding placements. Returns the accepted chunk count.</summary>
    public int UseCache(Resources.Terrain terrain, TerrainClusterData cache, TerrainGeometryIdentity identity)
    {
        if (_adapter is null || cache.Identity != identity || cache.VertexEncoding != _adapter.VertexEncoding
            || cache.MesherReadBorder != _mesher!.ReadBorder || cache.AttributeReadBorder != _adapter.ReadBorder
            || !cache.Fields.HeightFieldIds.AsSpan().SequenceEqual(_adapter.Fields.HeightFieldIds.AsSpan().ToArray().Distinct().Order().ToArray())
            || !cache.Fields.VoxelFieldIds.AsSpan().SequenceEqual(_adapter.Fields.VoxelFieldIds.AsSpan().ToArray().Distinct().Order().ToArray())) return 0;
        int count = 0;
        if (_subscriptions.Add(terrain)) terrain.Changed += CollectChange;
        foreach (TerrainClusterMesh cached in cache.Meshes)
        {
            TerrainClusterEntry entry = cached.Entry;
            if (entry.CellCount != Grid.CellCount || MathF.ScaleB(entry.CellSize, -entry.Lod) != Grid.CellSize
                || !cache.MatchesSources(terrain, entry)) continue;
            GeometryData geometry = GetGeometry(terrain, new(new(entry.X, entry.Y, entry.Z), entry.CellCount,
                entry.CellSize, entry.Lod, (TerrainTransitionFaces)entry.TransitionFaces));
            geometry.Mesh = cached.Mesh?.Duplicate();
            geometry.Revision = entry.Revision;
            geometry.Cached = true;
            _dirtyGeometry.Remove(geometry.Id);
            count++;
        }
        return count;
    }

    /// <summary>Waits for submitted builds. TerrainCommitSystem applies their results at the next game update.</summary>
    public ValueTask WaitForBuildsAsync() => new(Task.WhenAll(
        _batches.Values.Select(static batch => batch.Work.Completion.WaitAsync().AsTask())));

    internal void Bind(World world)
    {
        _world = world;
        _chunkQuery = world.Query(new QueryDefinitionBuilder().Read<TerrainChunk>());
        _placementsChanged = true;
    }

    internal void Unbind()
    {
        foreach (Resources.Terrain terrain in _subscriptions) terrain.Changed -= CollectChange;
        _subscriptions.Clear();
        foreach (ChunkData chunk in _chunks.Values) _world!.DestroyEntity(chunk.Entity);
        _world!.ReleaseQuery(_chunkQuery);
        _world = null;
        _chunks.Clear();
        _geometryByKey.Clear();
        _geometryById.Clear();
        _batches.Clear();
        _gpuBuilds.Clear();
        _dirtyGeometry.Clear();
        _changes.Clear();
        foreach (PlacementData placement in _placements.Values) placement.Created = false;
    }

    internal void CommitChanges()
    {
        var completed = new HashSet<int>();
        foreach ((Resources.Terrain terrain, MeshBatch batch) in _batches.ToArray())
        {
            if (!batch.Work.Completion.IsCompleted) continue;
            batch.Work.Completion.Complete();
            foreach (ChunkBuild build in batch.Chunks)
            {
                if (!_geometryById.TryGetValue(build.Geometry.Id, out GeometryData? geometry)) continue;
                if (geometry.Mesh is { } mesh && build.Mesh is { } replacement)
                    mesh.Update(replacement);
                else
                    geometry.Mesh = build.Mesh;
                geometry.Revision = batch.Work.Revision;
                completed.Add(geometry.Id);
            }
            ApplyPlacements(batch.Placements);
            _batches.Remove(terrain);
        }
        ApplyGeometry(completed);

        while (_changes.TryDequeue(out TerrainChange? change))
        foreach (GeometryData geometry in _geometryById.Values)
        {
            if (!ReferenceEquals(geometry.Key.Terrain, change.Terrain) || geometry.Revision >= change.Revision) continue;
            if (_batches.TryGetValue(change.Terrain, out MeshBatch? batch)
                && batch.Work.Revision >= change.Revision && batch.GeometryIds.Contains(geometry.Id)) continue;
            TerrainChunkBounds bounds = Grid.GetBounds(geometry.Key.Coordinates, geometry.Key.Lod);
            float border = ReadBorder * bounds.CellSize;
            TerrainSurface? surface = change.Terrain.Surface;
            TerrainFieldInputs fields = GpuAdapter?.Attributes.Fields ?? _adapter?.Fields ?? default;
            bool attributeChanged = change switch
            {
                HeightFieldChange height => fields.HeightFieldIds.AsSpan().Contains(height.Field.Id) && height.Affects(bounds.Volume, border),
                VoxelFieldChange voxel => fields.VoxelFieldIds.AsSpan().Contains(voxel.Field.Id) && voxel.Affects(bounds.Volume, border),
                _ => false,
            };
            if (surface is null || change.Affects(surface, bounds.Volume, border) || attributeChanged) _dirtyGeometry.Add(geometry.Id);
        }
    }

    private void ApplyGeometry(HashSet<int> completed)
    {
        if (completed.Count == 0) return;
        var updates = new List<(Entity Entity, TerrainChunk Chunk, Mesh? Mesh)>();
        _world!.ExecuteQuery(_chunkQuery, query =>
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                ReadOnlySpan<TerrainChunk> values = chunk.Read<TerrainChunk>();
                for (int row = 0; row < values.Length; row++)
                {
                    TerrainChunk value = values[row];
                    if (!completed.Contains(value.GeometryId)) continue;
                    GeometryData geometry = _geometryById[value.GeometryId];
                    updates.Add((chunk.Entities[row], value with { Revision = geometry.Revision }, geometry.Mesh));
                }
            }
        });
        foreach (var update in updates)
        {
            _world.Replace(update.Entity, update.Chunk);
            if (update.Mesh is { } mesh)
            {
                if (!_world.Has<MeshInstance>(update.Entity))
                    _world.Add(update.Entity, new MeshInstance { Mesh = mesh });
                else if (!ReferenceEquals(_world.Read<MeshInstance>(update.Entity).Mesh, mesh))
                    _world.Replace(update.Entity, new MeshInstance { Mesh = mesh });
            }
            else if (_world.Has<MeshInstance>(update.Entity))
                _world.Remove<MeshInstance>(update.Entity);
        }
    }

    internal void SynchronizePlacements(Vector3 cameraPosition)
    {
        if (_placementsChanged)
        {
            foreach ((ChunkKey key, ChunkData chunk) in _chunks.ToArray())
                if (!_placements.ContainsKey(key.PlacementId))
                {
                    _world!.DestroyEntity(chunk.Entity);
                    _chunks.Remove(key);
                }
            foreach (Resources.Terrain terrain in _subscriptions.ToArray())
                if (!_placements.Values.Any(placement => ReferenceEquals(placement.Value.Terrain, terrain))
                    && !_geometryById.Values.Any(geometry => geometry.Cached && ReferenceEquals(geometry.Key.Terrain, terrain)))
                {
                    terrain.Changed -= CollectChange;
                    _subscriptions.Remove(terrain);
                }
            _placementsChanged = false;
        }

        foreach ((int id, PlacementData data) in _placements)
        {
            TerrainPlacement placement = data.Value;
            if (_subscriptions.Add(placement.Terrain)) placement.Terrain.Changed += CollectChange;
            data.Selection = TerrainLodSelection.Select(placement, Grid, cameraPosition).Select(bounds =>
                GetGeometry(placement.Terrain, bounds)).ToArray();
            if (!data.Created)
            {
                foreach (GeometryData geometry in data.Selection)
                    CreateChunk(id, geometry, placement.Transform, placement.Materials);
                data.Created = true;
                data.AppliedRevision = placement.Revision;
            }
        }

        HashSet<int> used = _chunks.Values.Select(static chunk => chunk.GeometryId).ToHashSet();
        foreach (PlacementData placement in _placements.Values)
        foreach (GeometryData geometry in placement.Selection) used.Add(geometry.Id);
        foreach (MeshBatch batch in _batches.Values)
        foreach (PlacementBuild placement in batch.Placements)
        foreach (GeometryData geometry in placement.Selection) used.Add(geometry.Id);
        foreach ((GeometryKey key, GeometryData geometry) in _geometryByKey.ToArray())
            if (!geometry.Cached && !used.Contains(geometry.Id))
            {
                _geometryByKey.Remove(key);
                _geometryById.Remove(geometry.Id);
                _dirtyGeometry.Remove(geometry.Id);
            }
    }

    private GeometryData GetGeometry(Resources.Terrain terrain, TerrainChunkBounds bounds)
    {
        var key = new GeometryKey(terrain, bounds.Coordinates, bounds.Lod, bounds.TransitionFaces);
        if (!_geometryByKey.TryGetValue(key, out GeometryData? geometry))
        {
            geometry = new GeometryData(++_nextGeometryId, key);
            _geometryByKey.Add(key, geometry);
            _geometryById.Add(geometry.Id, geometry);
            _dirtyGeometry.Add(geometry.Id);
        }
        return geometry;
    }

    private void CreateChunk(int placementId, GeometryData geometry, TransformQvvs transform, IReadOnlyList<Material> materials)
    {
        GeometryKey key = geometry.Key;
        Entity entity = _world!.CreateEntity(new TerrainChunk(placementId, key.Coordinates, key.Lod,
            geometry.Revision, geometry.Id, key.TransitionFaces));
        _world.Add(entity, new TerrainSource(key.Terrain));
        if (GpuAdapter is not null) _world.Add(entity, new TerrainGpuSource(RenderKey));
        _world.Add(entity, new WorldTransform { Qvvs = transform });
        _world.AddBuffer<MeshMaterialBinding>(entity);
        SetMaterials(entity, materials);
        if (geometry.Mesh is { } mesh) _world.Add(entity, new MeshInstance { Mesh = mesh });
        _chunks.Add(new(placementId, key.Coordinates, key.Lod), new(entity, geometry.Id));
    }

    private bool MatchesSelection(int placementId, GeometryData[] selection)
    {
        if (_chunks.Keys.Count(key => key.PlacementId == placementId) != selection.Length) return false;
        foreach (GeometryData geometry in selection)
            if (!_chunks.TryGetValue(new(placementId, geometry.Key.Coordinates, geometry.Key.Lod), out ChunkData chunk)
                || chunk.GeometryId != geometry.Id) return false;
        return true;
    }

    private void ApplyPlacements(PlacementBuild[] placements)
    {
        foreach (PlacementBuild placement in placements)
        {
            if (!_placements.TryGetValue(placement.Id, out PlacementData? data)) continue;
            var selected = placement.Selection.Select(geometry => new ChunkKey(placement.Id,
                geometry.Key.Coordinates, geometry.Key.Lod)).ToHashSet();
            foreach ((ChunkKey key, ChunkData chunk) in _chunks.ToArray())
                if (key.PlacementId == placement.Id && !selected.Contains(key))
                {
                    _world!.DestroyEntity(chunk.Entity);
                    _chunks.Remove(key);
                }
            foreach (GeometryData geometry in placement.Selection)
            {
                GeometryKey key = geometry.Key;
                var chunkKey = new ChunkKey(placement.Id, key.Coordinates, key.Lod);
                if (!_chunks.TryGetValue(chunkKey, out ChunkData chunk))
                    CreateChunk(placement.Id, geometry, placement.Transform, placement.Materials);
                else
                {
                    _chunks[chunkKey] = chunk with { GeometryId = geometry.Id };
                    _world!.Replace(chunk.Entity, new TerrainChunk(placement.Id, key.Coordinates, key.Lod,
                        geometry.Revision, geometry.Id, key.TransitionFaces));
                    _world.Replace(chunk.Entity, new WorldTransform { Qvvs = placement.Transform });
                    SetMaterials(chunk.Entity, placement.Materials);
                    if (geometry.Mesh is { } mesh)
                    {
                        if (_world.Has<MeshInstance>(chunk.Entity))
                            _world.Replace(chunk.Entity, new MeshInstance { Mesh = mesh });
                        else _world.Add(chunk.Entity, new MeshInstance { Mesh = mesh });
                    }
                    else if (_world.Has<MeshInstance>(chunk.Entity)) _world.Remove<MeshInstance>(chunk.Entity);
                }
            }
            data.AppliedRevision = placement.Revision;
        }
    }

    private void SetMaterials(Entity entity, IReadOnlyList<Material> materials) =>
        _world!.ExecuteBufferWrite<MeshMaterialBinding>(entity, values =>
        {
            values.Clear();
            foreach (Material material in materials) values.Add(new(material));
        });

    internal void ScheduleBuilds()
    {
        foreach (var group in _placements.GroupBy(static pair => pair.Value.Value.Terrain))
        {
            Resources.Terrain terrain = group.Key;
            if (_batches.ContainsKey(terrain)) continue;
            ChunkBuild[] chunks = group.SelectMany(static pair => pair.Value.Selection).Distinct()
                .Where(geometry => _dirtyGeometry.Contains(geometry.Id)).Select(geometry => new ChunkBuild(geometry,
                    Grid.GetBounds(geometry.Key.Coordinates, geometry.Key.Lod) with { TransitionFaces = geometry.Key.TransitionFaces })).ToArray();
            if (chunks.Length == 0 && group.All(pair => pair.Value.AppliedRevision == pair.Value.Value.Revision
                && MatchesSelection(pair.Key, pair.Value.Selection))) continue;
            PlacementBuild[] placements = group.Select(pair => new PlacementBuild(pair.Key, pair.Value.Value.Revision,
                pair.Value.Value.Transform, pair.Value.Value.Materials.ToArray(), pair.Value.Selection)).ToArray();
            TerrainSurfaceJob work;
            if (chunks.Length == 0) work = new(terrain.Revision, default);
            else if (GpuAdapter is { } gpu)
            {
                TerrainGeometryRequest request = gpu.Capture(RenderKey, terrain,
                    chunks.Select(static chunk => new TerrainGpuChunkBuild(chunk.Geometry.Id, chunk.Bounds)).ToArray());
                _gpuBuilds[terrain] = request;
                work = new(request.Revision, request.Completion.Handle);
            }
            else if (terrain.Surface is { } surface)
            {
                int border = ReadBorder;
                TerrainBounds bounds = chunks[0].Bounds.Volume.Expand(border * chunks[0].Bounds.CellSize);
                foreach (ChunkBuild chunk in chunks.AsSpan(1))
                    bounds = bounds.Union(chunk.Bounds.Volume.Expand(border * chunk.Bounds.CellSize));
                work = surface.ScheduleRead(bounds, _adapter!.Fields, new MeshWork(chunks, _mesher!, _adapter), chunks.Length);
            }
            else
                work = new(terrain.Revision, default);
            _batches.Add(terrain, new MeshBatch(work, chunks, placements));
            foreach (ChunkBuild chunk in chunks) _dirtyGeometry.Remove(chunk.Geometry.Id);
        }
    }

    private void CollectChange(TerrainChange change) => _changes.Enqueue(change);
    private int ReadBorder => GpuAdapter is { } gpu ? gpu.ReadBorder : _mesher!.ReadBorder + _adapter!.ReadBorder;

    private readonly record struct ChunkKey(int PlacementId, TerrainChunkCoordinate Coordinates, int Lod);
    private readonly record struct GeometryKey(Resources.Terrain Terrain, TerrainChunkCoordinate Coordinates, int Lod,
        TerrainTransitionFaces TransitionFaces);
    private readonly record struct ChunkData(Entity Entity, int GeometryId);

    private sealed class PlacementData(TerrainPlacement value)
    {
        public TerrainPlacement Value { get; } = value;
        public bool Created;
        public uint AppliedRevision;
        public GeometryData[] Selection = [];
    }

    private sealed class GeometryData(int id, GeometryKey key)
    {
        public int Id { get; } = id;
        public GeometryKey Key { get; } = key;
        public Mesh? Mesh;
        public ulong Revision;
        public bool Cached;
    }

    private sealed class ChunkBuild(GeometryData geometry, TerrainChunkBounds bounds)
    {
        public GeometryData Geometry { get; } = geometry;
        public TerrainChunkBounds Bounds { get; } = bounds;
        public Mesh? Mesh;
    }

    private sealed record PlacementBuild(int Id, uint Revision, TransformQvvs Transform,
        Material[] Materials, GeometryData[] Selection);

    private sealed class MeshBatch(TerrainSurfaceJob work, ChunkBuild[] chunks, PlacementBuild[] placements)
    {
        public TerrainSurfaceJob Work { get; } = work;
        public ChunkBuild[] Chunks { get; } = chunks;
        public PlacementBuild[] Placements { get; } = placements;
        public HashSet<int> GeometryIds { get; } = chunks.Select(static chunk => chunk.Geometry.Id).ToHashSet();
    }

    private readonly struct MeshWork(ChunkBuild[] chunks, ITerrainMesher mesher, ITerrainClusterAdapter adapter)
        : ITerrainSurfaceWork
    {
        public void Execute(TerrainSurfaceRead surface, int index)
        {
            ChunkBuild chunk = chunks[index];
            TerrainSurfaceMesh geometry = mesher.Build(surface, chunk.Bounds);
            chunk.Mesh = adapter.Build(surface, geometry, $"Terrain chunk {chunk.Bounds.Coordinates}");
        }
    }
}
