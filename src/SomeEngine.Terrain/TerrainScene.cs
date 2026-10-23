using System.Collections.Concurrent;
using SomeEngine.Core.ECS.Components;
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
            if (surface is null || change.Affects(surface, bounds.Volume, border)) _dirtyGeometry.Add(geometry.Id);
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

    internal void SynchronizePlacements()
    {
        if (_placementsChanged)
        {
            foreach ((ChunkKey key, ChunkData chunk) in _chunks.ToArray())
                if (!_placements.ContainsKey(key.PlacementId))
                {
                    _world!.DestroyEntity(chunk.Entity);
                    _chunks.Remove(key);
                }
            HashSet<int> used = _chunks.Values.Select(static chunk => chunk.GeometryId).ToHashSet();
            foreach ((GeometryKey key, GeometryData geometry) in _geometryByKey.ToArray())
                if (!used.Contains(geometry.Id))
                {
                    _geometryByKey.Remove(key);
                    _geometryById.Remove(geometry.Id);
                    _dirtyGeometry.Remove(geometry.Id);
                }
            foreach (Resources.Terrain terrain in _subscriptions.ToArray())
                if (!_placements.Values.Any(placement => ReferenceEquals(placement.Value.Terrain, terrain)))
                {
                    terrain.Changed -= CollectChange;
                    _subscriptions.Remove(terrain);
                }
            _placementsChanged = false;
        }

        foreach ((int id, PlacementData data) in _placements)
        {
            TerrainPlacement placement = data.Value;
            if (!data.Created)
            {
                if (_subscriptions.Add(placement.Terrain)) placement.Terrain.Changed += CollectChange;
                TerrainChunkRange range = placement.Chunks;
                for (int z = range.MinZ; z < range.MaxZ; z++)
                for (int y = range.MinY; y < range.MaxY; y++)
                for (int x = range.MinX; x < range.MaxX; x++)
                    CreateChunk(id, placement, new(x, y, z));
                data.Created = true;
                data.AppliedRevision = placement.Revision;
            }
            else if (data.AppliedRevision != placement.Revision)
            {
                foreach ((ChunkKey key, ChunkData chunk) in _chunks)
                    if (key.PlacementId == id)
                    {
                        _world!.Replace(chunk.Entity, new WorldTransform { Qvvs = placement.Transform });
                        SetMaterials(chunk.Entity, placement.Materials);
                    }
                data.AppliedRevision = placement.Revision;
            }
        }
    }

    private void CreateChunk(int placementId, TerrainPlacement placement, TerrainChunkCoordinate coordinates)
    {
        var key = new GeometryKey(placement.Terrain, coordinates, placement.Lod);
        if (!_geometryByKey.TryGetValue(key, out GeometryData? geometry))
        {
            geometry = new GeometryData(++_nextGeometryId, key);
            _geometryByKey.Add(key, geometry);
            _geometryById.Add(geometry.Id, geometry);
            _dirtyGeometry.Add(geometry.Id);
        }
        Entity entity = _world!.CreateEntity(new TerrainChunk(placementId, coordinates, placement.Lod,
            geometry.Revision, geometry.Id));
        _world.Add(entity, new TerrainSource(placement.Terrain));
        if (GpuAdapter is not null) _world.Add(entity, new TerrainGpuSource(RenderKey));
        _world.Add(entity, new WorldTransform { Qvvs = placement.Transform });
        _world.AddBuffer<MeshMaterialBinding>(entity);
        SetMaterials(entity, placement.Materials);
        if (geometry.Mesh is { } mesh) _world.Add(entity, new MeshInstance { Mesh = mesh });
        _chunks.Add(new(placementId, coordinates, placement.Lod), new(entity, geometry.Id));
    }

    private void SetMaterials(Entity entity, IReadOnlyList<Material> materials) =>
        _world!.ExecuteBufferWrite<MeshMaterialBinding>(entity, values =>
        {
            values.Clear();
            foreach (Material material in materials) values.Add(new(material));
        });

    internal void ScheduleBuilds()
    {
        foreach (var group in _dirtyGeometry.Select(id => _geometryById[id])
            .GroupBy(static geometry => geometry.Key.Terrain).ToArray())
        {
            Resources.Terrain terrain = group.Key;
            if (_batches.ContainsKey(terrain)) continue;
            ChunkBuild[] chunks = group.Select(geometry => new ChunkBuild(geometry,
                Grid.GetBounds(geometry.Key.Coordinates, geometry.Key.Lod))).ToArray();
            TerrainSurfaceJob work;
            if (GpuAdapter is { } gpu)
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
                work = surface.ScheduleRead(bounds, new MeshWork(chunks, _mesher!, _adapter!), chunks.Length);
            }
            else
                work = new(terrain.Revision, default);
            _batches.Add(terrain, new MeshBatch(work, chunks));
            foreach (ChunkBuild chunk in chunks) _dirtyGeometry.Remove(chunk.Geometry.Id);
        }
    }

    private void CollectChange(TerrainChange change) => _changes.Enqueue(change);
    private int ReadBorder => GpuAdapter is { } gpu ? gpu.ReadBorder : _mesher!.ReadBorder + _adapter!.ReadBorder;

    private readonly record struct ChunkKey(int PlacementId, TerrainChunkCoordinate Coordinates, int Lod);
    private readonly record struct GeometryKey(Resources.Terrain Terrain, TerrainChunkCoordinate Coordinates, int Lod);
    private readonly record struct ChunkData(Entity Entity, int GeometryId);

    private sealed class PlacementData(TerrainPlacement value)
    {
        public TerrainPlacement Value { get; } = value;
        public bool Created;
        public uint AppliedRevision;
    }

    private sealed class GeometryData(int id, GeometryKey key)
    {
        public int Id { get; } = id;
        public GeometryKey Key { get; } = key;
        public Mesh? Mesh;
        public ulong Revision;
    }

    private sealed class ChunkBuild(GeometryData geometry, TerrainChunkBounds bounds)
    {
        public GeometryData Geometry { get; } = geometry;
        public TerrainChunkBounds Bounds { get; } = bounds;
        public Mesh? Mesh;
    }

    private sealed class MeshBatch(TerrainSurfaceJob work, ChunkBuild[] chunks)
    {
        public TerrainSurfaceJob Work { get; } = work;
        public ChunkBuild[] Chunks { get; } = chunks;
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
