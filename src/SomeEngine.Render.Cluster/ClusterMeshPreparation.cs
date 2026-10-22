using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Cluster;

/// <summary>
/// Finds the unique meshes referenced by RenderWorld mesh entities and registers their retained
/// streamed payload sources with the active Cluster residency epoch.
/// </summary>
internal sealed class ClusterMeshPrepareSystem : IDisposable
{
    private readonly RenderWorld _world;
    private readonly ClusterResidency _residency;
    private readonly QueryHandle _meshQuery;
    private readonly EntityTableChanges _tableChanges;
    private readonly Dictionary<Entity, RenderMesh> _entityMeshes = [];
    private readonly HashSet<Entity> _geometryChanges = [];
    private readonly HashSet<Mesh> _unusedMeshes = [];
    private readonly Dictionary<Mesh, int> _meshReferences = [];
    private readonly Dictionary<Mesh, RenderMesh> _missing = [];
    private uint _lastVersion;
    private int _preparing;
    private int _disposed;

    internal ClusterMeshPrepareSystem(RenderWorld world, ClusterResidency residency)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _residency = residency;
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
            _residency.ReleaseUnusedMeshes();
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
        if (_entityMeshes.TryGetValue(entity, out RenderMesh previous))
        {
            if (previous == value) return;
            _entityMeshes.Remove(entity);
            int count = _meshReferences[previous.Mesh] - 1;
            if (count == 0)
            {
                _meshReferences.Remove(previous.Mesh);
                _missing.Remove(previous.Mesh);
                _unusedMeshes.Add(previous.Mesh);
            }
            else _meshReferences[previous.Mesh] = count;
        }
        else if (mesh is null) return;
        _geometryChanges.Add(entity);
        if (mesh is null) return;
        _unusedMeshes.Remove(mesh);
        _residency.KeepMesh(mesh);
        _entityMeshes.Add(entity, value!.Value);
        _meshReferences.TryGetValue(mesh, out int references);
        _meshReferences[mesh] = references + 1;
        if (references == 0 && !_residency.IsMeshRegistered(mesh)) _missing.TryAdd(mesh, value!.Value);
    }

    private async ValueTask<ClusterMeshPrepareResult> PrepareMissingAsync(
        CancellationToken cancellationToken)
    {
        int registered = 0;
        try
        {
            RenderMesh[] missingMeshes = [.. _missing.Values];
            var lowestDetailPageIds = new HashSet<uint>();
            foreach (RenderMesh mesh in missingMeshes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClusterMeshRegistrationResult result =
                    await _residency.RegisterMeshAsync(mesh.Mesh, cancellationToken, mesh.PageSource).ConfigureAwait(false);
                if (result.Added)
                    registered++;
                lowestDetailPageIds.UnionWith(result.Registration.LowestDetailPageIds);
            }
            await _residency.LoadOpeningPagesAsync(
                lowestDetailPageIds,
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

    internal void ApplyGeometry()
    {
        foreach (Entity entity in _geometryChanges.ToArray())
        {
            if (_entityMeshes.TryGetValue(entity, out RenderMesh mesh))
            {
                if (!_residency.TryGetGeometry(mesh.Mesh, out ClusterGeometry geometry)) continue;
                var value = new RenderClusterGeometry(geometry, mesh.BoundsExpansion);
                if (_world.Has<RenderClusterGeometry>(entity)) _world.Replace(entity, value);
                else _world.Add(entity, value);
            }
            else if (_world.IsAlive(entity) && _world.Has<RenderClusterGeometry>(entity))
                _world.Remove<RenderClusterGeometry>(entity);
            _geometryChanges.Remove(entity);
        }
        if (_geometryChanges.Count == 0)
        {
            foreach (Mesh mesh in _unusedMeshes) _residency.MarkMeshUnused(mesh);
            _unusedMeshes.Clear();
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
            _geometryChanges.Clear();
            _unusedMeshes.Clear();
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
