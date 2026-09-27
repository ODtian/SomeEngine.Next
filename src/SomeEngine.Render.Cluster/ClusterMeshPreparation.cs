using SomeEngine.Resources;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Cluster;

/// <summary>
/// Deduplicates Cluster geometry registration by immutable content version. It does not publish pending roots;
/// publication remains an explicit operation on <see cref="ClusterResidency"/>.
/// </summary>
internal sealed class ClusterMeshCache
{
    private readonly ClusterResidency _residency;
    private readonly object _instanceGeometryGate = new();
    private RenderMesh[] _instanceGeometry = [];
    private int[] _instanceEntityGenerations = [];
    private uint[] _instanceRoots = [];

    internal ClusterMeshCache(ClusterResidency residency)
        => _residency = residency ?? throw new ArgumentNullException(nameof(residency));

    internal ClusterResidency Residency => _residency;

    internal RenderTimeline Timeline => _residency.Timeline;

    internal ClusterEpochId EpochId => _residency.EpochId;

    internal int PublishedMeshCount => _residency.PublishedMeshCount;

    internal ClusterMeshesSnapshot CaptureSnapshot() => _residency.CaptureMeshesSnapshot();

    internal bool IsRegistered(Mesh mesh)
        => _residency.IsMeshRegistered(mesh);

    internal ValueTask<ClusterMeshRegistrationResult> PrepareAsync(
        RenderMesh mesh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh.Mesh);
        return _residency.RegisterMeshAsync(mesh.Mesh, cancellationToken, mesh.PageSource);
    }

    internal ValueTask LoadOpeningPagesAsync(
        IReadOnlyCollection<uint> lowestDetailPageIds,
        IReadOnlyCollection<uint> allPageIds,
        CancellationToken cancellationToken = default) =>
        _residency.LoadOpeningPagesAsync(lowestDetailPageIds, allPageIds, cancellationToken);

    internal bool TryGetPublishedRoot(Mesh mesh, out uint root)
    {
        if (_residency.TryGetPublishedRoot(mesh, out root))
            return true;
        root = ClusterRenderFeature.MissingBvhRoot;
        return false;
    }

    internal void ResolveInstanceGeometry(
        ReadOnlySpan<Entity> entities,
        ReadOnlySpan<RenderMesh> meshes,
        Span<uint> roots,
        Span<float> boundsExpansions)
    {
        if (entities.Length != meshes.Length ||
            roots.Length != meshes.Length ||
            boundsExpansions.Length != meshes.Length)
        {
            throw new ArgumentException(
                "Cluster instance geometry inputs and destinations must have identical lengths.");
        }

        lock (_instanceGeometryGate)
        {
            int required = 0;
            for (int row = 0; row < entities.Length; row++)
                required = Math.Max(required, checked(entities[row].Index + 1));
            EnsureInstanceGeometryCapacity(required);

            Mesh? lastMesh = null;
            uint lastRoot = ClusterRenderFeature.MissingBvhRoot;
            bool lastRootPublished = false;
            for (int row = 0; row < entities.Length; row++)
            {
                Entity entity = entities[row];
                RenderMesh geometry = meshes[row];
                int index = entity.Index;
                uint root;
                bool published;
                if (_instanceEntityGenerations[index] == entity.Generation &&
                    _instanceGeometry[index] == geometry)
                {
                    root = _instanceRoots[index];
                    published = true;
                }
                else if (lastRootPublished &&
                    lastMesh == geometry.Mesh)
                {
                    root = lastRoot;
                    published = true;
                }
                else
                {
                    published = TryGetPublishedRoot(geometry.Mesh, out root);
                }

                if (published)
                {
                    _instanceGeometry[index] = geometry;
                    _instanceEntityGenerations[index] = entity.Generation;
                    _instanceRoots[index] = root;
                    lastMesh = geometry.Mesh;
                    lastRoot = root;
                    lastRootPublished = true;
                }
                else
                {
                    _instanceEntityGenerations[index] = 0;
                    root = ClusterRenderFeature.MissingBvhRoot;
                    lastRootPublished = false;
                }

                roots[row] = root;
                boundsExpansions[row] = geometry.BoundsExpansion;
            }
        }
    }

    private void EnsureInstanceGeometryCapacity(int required)
    {
        if (_instanceRoots.Length >= required)
            return;
        int capacity = Math.Max(required, Math.Max(16, _instanceRoots.Length * 2));
        Array.Resize(ref _instanceGeometry, capacity);
        Array.Resize(ref _instanceEntityGenerations, capacity);
        Array.Resize(ref _instanceRoots, capacity);
    }
}

/// <summary>
/// Finds the unique meshes referenced by RenderWorld mesh entities and registers their retained
/// streamed payload sources with the active Cluster residency epoch.
/// </summary>
internal sealed class ClusterMeshPrepareSystem : IDisposable
{
    private readonly RenderWorld _world;
    private readonly ClusterMeshCache _cache;
    private readonly QueryHandle _meshQuery;
    private readonly EntityTableChanges _tableChanges;
    private readonly Dictionary<Entity, Mesh> _entityMeshes = [];
    private readonly Dictionary<Mesh, int> _meshReferences = [];
    private readonly Dictionary<Mesh, RenderMesh> _missing = [];
    private uint _lastVersion;
    private int _preparing;
    private int _disposed;

    internal ClusterMeshPrepareSystem(RenderWorld world, ClusterMeshCache cache)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _meshQuery = world.Query(new QueryDefinitionBuilder().Read<RenderMesh>());
        try { _tableChanges = world.ObserveEntityTables(); }
        catch { world.ReleaseQuery(_meshQuery); throw; }
    }

    internal ValueTask<ClusterMeshPrepareResult> PrepareAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _preparing, 1, 0) != 0)
            throw new InvalidOperationException("Cluster mesh preparation is already in progress.");
        if (Volatile.Read(ref _disposed) != 0)
        {
            Volatile.Write(ref _preparing, 0);
            throw new ObjectDisposedException(nameof(ClusterMeshPrepareSystem));
        }

        try
        {
            EntityTableChangeBatch membership = _tableChanges.Read();
            foreach (Entity entity in membership.Entities)
            {
                if (!_world.IsAlive(entity) || !_world.Has<RenderMesh>(entity))
                    SetMesh(entity, null);
                else
                    SetMesh(entity, _world.Read<RenderMesh>(entity));
            }
            uint version = _lastVersion;
            _world.ExecuteQuery(_meshQuery, _lastVersion, cursor =>
            {
                version = cursor.CurrentSystemVersion;
                foreach (QueryChunkView chunk in cursor.Chunks)
                {
                    if (!chunk.HasChangedSinceLastSystemVersion<RenderMesh>()) continue;
                    ReadOnlySpan<RenderMesh> meshes = chunk.Read<RenderMesh>();
                    for (int row = 0; row < meshes.Length; row++)
                        if (chunk.RowChangedSinceLastSystemVersion<RenderMesh>(row))
                            SetMesh(chunk.Entities[row], meshes[row]);
                }
            });
            // These indexes describe observed input only. Registration obligations remain in
            // _missing until each registration succeeds, even when a later operation fails.
            _tableChanges.Accept(membership);
            _lastVersion = version;
            if (_missing.Count == 0)
            {
                Volatile.Write(ref _preparing, 0);
                return ValueTask.FromResult(new ClusterMeshPrepareResult(_meshReferences.Count, 0, 0));
            }
            return PrepareMissingAsync(cancellationToken);
        }
        catch
        {
            Volatile.Write(ref _preparing, 0);
            throw;
        }
    }

    private void SetMesh(Entity entity, RenderMesh? value)
    {
        Mesh? mesh = value?.Mesh;
        if (_entityMeshes.TryGetValue(entity, out Mesh? previous))
        {
            if (ReferenceEquals(previous, mesh)) return;
            _entityMeshes.Remove(entity);
            int count = _meshReferences[previous] - 1;
            if (count == 0)
            {
                _meshReferences.Remove(previous);
                _missing.Remove(previous);
            }
            else _meshReferences[previous] = count;
        }
        if (mesh is null) return;
        _entityMeshes.Add(entity, mesh);
        _meshReferences.TryGetValue(mesh, out int references);
        _meshReferences[mesh] = references + 1;
        if (references == 0 && !_cache.IsRegistered(mesh)) _missing.TryAdd(mesh, value!.Value);
    }

    private async ValueTask<ClusterMeshPrepareResult> PrepareMissingAsync(
        CancellationToken cancellationToken)
    {
        int registered = 0;
        try
        {
            RenderMesh[] missingMeshes = [.. _missing.Values];
            var lowestDetailPageIds = new HashSet<uint>();
            var allPageIds = new HashSet<uint>();
            foreach (RenderMesh mesh in missingMeshes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClusterMeshRegistrationResult result =
                    await _cache.PrepareAsync(mesh, cancellationToken).ConfigureAwait(false);
                if (result.Added)
                    registered++;
                lowestDetailPageIds.UnionWith(result.Registration.LowestDetailPageIds);
                uint end = checked(result.Registration.FirstPageId + result.Registration.PageCount);
                for (uint pageId = result.Registration.FirstPageId; pageId < end; pageId++)
                    allPageIds.Add(pageId);
            }
            await _cache.LoadOpeningPagesAsync(
                lowestDetailPageIds,
                allPageIds,
                cancellationToken).ConfigureAwait(false);
            foreach (RenderMesh mesh in missingMeshes)
                _missing.Remove(mesh.Mesh);
            return new ClusterMeshPrepareResult(_meshReferences.Count, registered, 0);
        }
        finally
        {
            Volatile.Write(ref _preparing, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;
        try
        {
            if (Volatile.Read(ref _preparing) != 0)
            {
                throw new InvalidOperationException(
                    "Cannot dispose Cluster mesh preparation during an active scan.");
            }
            _world.ReleaseQuery(_meshQuery);
            _tableChanges.Dispose();
            _entityMeshes.Clear();
            _meshReferences.Clear();
            _missing.Clear();
            Volatile.Write(ref _disposed, 2);
        }
        catch
        {
            Volatile.Write(ref _disposed, 0);
            throw;
        }
    }
}
