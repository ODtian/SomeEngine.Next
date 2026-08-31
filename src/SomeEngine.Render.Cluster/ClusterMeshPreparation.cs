using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Cluster;

/// <summary>
/// Deduplicates Cluster geometry registration by asset object. It does not publish pending roots;
/// publication remains an explicit operation on <see cref="ClusterResidency"/>.
/// </summary>
internal sealed class ClusterMeshCache
{
    private readonly ClusterResidency _residency;
    private readonly object _instanceGeometryGate = new();
    private RenderMesh[] _instanceGeometry = [];
    private ulong[] _instanceMeshRevisions = [];
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

    internal async ValueTask<bool> PrepareAsync(
        Mesh mesh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ClusterMeshRegistrationResult result = await _residency
            .RegisterMeshAsync(mesh, cancellationToken)
            .ConfigureAwait(false);
        return result.Added;
    }

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
            ulong lastRevision = 0;
            uint lastRoot = ClusterRenderFeature.MissingBvhRoot;
            bool lastRootPublished = false;
            for (int row = 0; row < entities.Length; row++)
            {
                Entity entity = entities[row];
                RenderMesh geometry = meshes[row];
                ulong revision = geometry.Mesh.Revision;
                int index = entity.Index;
                uint root;
                bool published;
                if (_instanceEntityGenerations[index] == entity.Generation &&
                    _instanceMeshRevisions[index] == revision &&
                    _instanceGeometry[index] == geometry)
                {
                    root = _instanceRoots[index];
                    published = true;
                }
                else if (lastRootPublished &&
                    lastRevision == revision &&
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
                    _instanceMeshRevisions[index] = revision;
                    _instanceEntityGenerations[index] = entity.Generation;
                    _instanceRoots[index] = root;
                    lastMesh = geometry.Mesh;
                    lastRevision = revision;
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
        Array.Resize(ref _instanceMeshRevisions, capacity);
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
    private readonly HashSet<Mesh> _seen = [];
    private readonly List<Mesh> _missing = [];
    private long _topologyRevision = -1;
    private int _referencedMeshCount;
    private int _preparing;
    private int _disposed;

    internal ClusterMeshPrepareSystem(RenderWorld world, ClusterMeshCache cache)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _meshQuery = world.Query(new QueryDefinitionBuilder().Read<RenderMesh>());
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
            long topologyRevision = _world.PublishedTopologyRevision;
            if (_topologyRevision == topologyRevision)
            {
                ClusterMeshesSnapshot snapshot = _cache.CaptureSnapshot();
                if (!snapshot.HasPendingPublication
                    && snapshot.RegisteredMeshCount == snapshot.PublishedMeshCount)
                {
                    Volatile.Write(ref _preparing, 0);
                    return ValueTask.FromResult(
                        new ClusterMeshPrepareResult(_referencedMeshCount, 0, 0));
                }
            }

            _world.ExecuteQuery(_meshQuery, cursor =>
            {
                foreach (QueryChunkView chunk in cursor.Chunks)
                {
                    ReadOnlySpan<RenderMesh> meshes = chunk.Read<RenderMesh>();
                    for (int row = 0; row < meshes.Length; row++)
                    {
                        Mesh mesh = meshes[row].Mesh;
                        if (!_seen.Add(mesh) || _cache.IsRegistered(mesh))
                            continue;
                        _missing.Add(mesh);
                    }
                }
            });
            int referencedMeshCount = _seen.Count;
            if (_missing.Count == 0)
            {
                _topologyRevision = topologyRevision;
                _referencedMeshCount = referencedMeshCount;
                ClearScratch();
                Volatile.Write(ref _preparing, 0);
                return ValueTask.FromResult(
                    new ClusterMeshPrepareResult(referencedMeshCount, 0, 0));
            }
            return PrepareMissingAsync(
                topologyRevision,
                referencedMeshCount,
                cancellationToken);
        }
        catch
        {
            ClearScratch();
            Volatile.Write(ref _preparing, 0);
            throw;
        }
    }

    private async ValueTask<ClusterMeshPrepareResult> PrepareMissingAsync(
        long topologyRevision,
        int referencedMeshCount,
        CancellationToken cancellationToken)
    {
        int registered = 0;
        int unresolved = 0;
        try
        {
            for (int index = 0; index < _missing.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await _cache
                    .PrepareAsync(_missing[index], cancellationToken)
                    .ConfigureAwait(false))
                {
                    registered++;
                }
            }

            if (unresolved == 0 && _world.PublishedTopologyRevision == topologyRevision)
            {
                _topologyRevision = topologyRevision;
                _referencedMeshCount = referencedMeshCount;
            }
            return new ClusterMeshPrepareResult(referencedMeshCount, registered, unresolved);
        }
        finally
        {
            ClearScratch();
            Volatile.Write(ref _preparing, 0);
        }
    }

    private void ClearScratch()
    {
        _missing.Clear();
        _seen.Clear();
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
            Volatile.Write(ref _disposed, 2);
        }
        catch
        {
            Volatile.Write(ref _disposed, 0);
            throw;
        }
    }
}
