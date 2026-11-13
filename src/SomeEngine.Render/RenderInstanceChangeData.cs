using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;
namespace SomeEngine.Render;
public readonly record struct RenderMeshLodChange(uint RootNode, Vector4 Bounds);
public sealed class RenderInstanceChangeData
{
    private readonly RenderWorld _world;
    private readonly IRenderInstanceBatchSource<RenderInstanceSingleGroup> _instances;
    private readonly QueryHandle _query;
    public RenderInstanceChangeData(RenderWorld world, IRenderInstanceBatchSource<RenderInstanceSingleGroup> instances, QueryHandle query)
    {
        _world = world;
        _instances = instances;
        _query = query;
    }

    public void RecordGeometryChanges(ulong sequence, IEnumerable<Mesh> meshes, IEnumerable<Mesh> lodMeshes)
    {
        if (sequence == _geometrySequence)
            return;
        var excluded = lodMeshes.ToHashSet();
        RecordChanges(meshes.Where(mesh => !excluded.Contains(mesh)).ToArray(), []);
        _geometrySequence = sequence;
    }

    public void SetStreamingQuality(float quality)
    {
        if (quality == _streamingQuality)
            return;
        RecordChanges([], [], all: true);
        _streamingQuality = quality;
    }

    internal DepthCacheClass[] _cacheClasses = [];
    internal RenderInstanceBatches<RenderInstanceSingleGroup>? _inputInstances;
    internal uint _inputVersion;
    internal bool _hasFirstPersonWorldMeshes;
    /// <summary>Reports world-space first-person representations in the current render entities.</summary>
    public bool HasFirstPersonWorldMeshes()
    {
        RefreshInputs();
        return _hasFirstPersonWorldMeshes;
    }

    internal void RefreshInputs()
    {
        RenderInstanceBatches<RenderInstanceSingleGroup> instances = _instances.Current!;
        bool membershipChanged = !ReferenceEquals(instances, _inputInstances);
        if (!membershipChanged && _inputVersion == _world.CurrentTick)
            return;
        var inputs = new InstanceInputChanges(this, instances, membershipChanged);
        _world.ExecuteQuery(_query, _inputVersion, ref inputs, static (QueryCursor query, ref InstanceInputChanges data) => data.Collect(query));
    }

    internal readonly struct InstanceInputChanges(RenderInstanceChangeData pipeline, RenderInstanceBatches<RenderInstanceSingleGroup> instances, bool membershipChanged)
    {
        internal void Collect(scoped QueryCursor query)
        {
            bool meshChanged = membershipChanged;
            if (!meshChanged)
                foreach (QueryChunkView chunk in query.Chunks)
                    if (chunk.HasChangedSinceLastSystemVersion<RenderClusterGeometry>() || (chunk.Has<RenderMesh>() && chunk.HasChangedSinceLastSystemVersion<RenderMesh>()))
                    {
                        meshChanged = true;
                        break;
                    }

            if (meshChanged)
            {
                if (pipeline._cacheClasses.Length != instances.AddressCount)
                    pipeline._cacheClasses = new DepthCacheClass[instances.AddressCount];
                bool firstPerson = false;
                int entityIndex = 0;
                foreach (QueryChunkView chunk in query.Chunks)
                {
                    bool hasMesh = chunk.TryRead<RenderMesh>(out ReadOnlySpan<RenderMesh> meshes);
                    for (int row = 0; row < chunk.Count; row++)
                    {
                        RenderMesh mesh = hasMesh ? meshes[row] : default;
                        firstPerson |= hasMesh && mesh.FirstPerson.Kind == FirstPersonMeshKind.WorldRepresentation;
                        foreach (var address in instances.RowsForEntity(entityIndex++))
                            pipeline._cacheClasses[address.Row] = hasMesh ? mesh.DepthCacheClass : DepthCacheClass.Auto;
                    }
                }

                pipeline._hasFirstPersonWorldMeshes = firstPerson;
                pipeline._inputInstances = instances;
            }

            pipeline._inputVersion = query.CurrentSystemVersion;
        }
    }

    internal sealed record InstanceChange(ulong Revision, Mesh[] Meshes, HashSet<Entity> Entities, bool All);
    internal readonly List<InstanceChange> _changes = [];
    internal ulong _revision;
    internal ulong _geometrySequence;
    internal float _streamingQuality = 1.0f;
    public void RecordChanges(Mesh[] meshes, HashSet<Entity> entities, bool all = false)
    {
        if (meshes.Length == 0 && entities.Count == 0 && !all)
            return;
        _revision = checked(_revision + 1);
        _changes.Add(new(_revision, meshes, entities, all));
        if (_changes.Count > 64)
            _changes.RemoveAt(0);
    }

    /// <summary>Collects current instance rows affected by uploaded geometry and extracted raster inputs.</summary>
    public bool CollectChangedInstances(ulong afterRevision, List<uint> rows, List<uint> streamingRows, out ulong revision)
    {
        revision = _revision;
        if (afterRevision == _revision)
            return true;
        if (_changes.Count == 0 || afterRevision + 1 < _changes[0].Revision)
            return false;
        var meshes = new HashSet<Mesh>();
        var entities = new HashSet<Entity>();
        bool all = false;
        foreach (InstanceChange change in _changes)
        {
            if (change.Revision <= afterRevision)
                continue;
            meshes.UnionWith(change.Meshes);
            entities.UnionWith(change.Entities);
            all |= change.All;
        }

        var changed = new ChangedInstances(_instances.Current!, meshes, entities, rows, streamingRows, all);
        _world.ExecuteQuery(_query, ref changed, static (QueryCursor query, ref ChangedInstances data) => data.Collect(query));
        return true;
    }

    internal struct ChangedInstances(RenderInstanceBatches<RenderInstanceSingleGroup> batches, HashSet<Mesh> meshes, HashSet<Entity> entities, List<uint> rows, List<uint> streamingRows, bool all)
    {
        private int _entityIndex;
        internal void Collect(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                bool materialChanged = entities.Contains(row.Entity);
                bool geometryChanged = row.TryRead<RenderMesh>(out var mesh) && meshes.Contains(mesh.Mesh);
                if (materialChanged || geometryChanged || all)
                    foreach (var address in batches.RowsForEntity(_entityIndex))
                        (materialChanged || geometryChanged ? rows : streamingRows).Add(checked((uint)address.Row));
                _entityIndex++;
            }
        }
    }

    /// <summary>Reads cache choices and the render-world version observed with them.</summary>
    public ReadOnlySpan<DepthCacheClass> CollectInstanceCacheInputs(ReadOnlySpan<RenderMeshLodChange> changes, List<Vector4> lodBounds, out uint version)
    {
        RefreshInputs();
        version = _inputVersion;
        if (!changes.IsEmpty)
        {
            var inputs = new InstanceLodBounds(_instances.Current!, changes, lodBounds);
            _world.ExecuteQuery(_query, ref inputs, static (scoped QueryCursor query, ref InstanceLodBounds data) => data.Collect(query));
        }

        return _cacheClasses;
    }

    /// <summary>Maps rebuilt caster rows to entities and reports actual transform or mesh writes.</summary>
    public void CollectInstanceEntities(Span<Entity> entities, uint afterVersion, List<uint> changedRows)
    {
        var inputs = new InstanceEntities(_instances.Current!, entities, changedRows);
        _world.ExecuteQuery(_query, afterVersion, ref inputs, static (scoped QueryCursor query, ref InstanceEntities data) => data.Collect(query));
    }

    internal ref struct InstanceEntities(RenderInstanceBatches<RenderInstanceSingleGroup> batches, Span<Entity> entities, List<uint> changedRows)
    {
        private readonly Span<Entity> _entities = entities;
        internal void Collect(scoped QueryCursor query)
        {
            int entityIndex = 0;
            foreach (QueryChunkView chunk in query.Chunks)
                for (int row = 0; row < chunk.Count; row++)
                {
                    bool changed = chunk.RowChangedSinceLastSystemVersion<RenderTransform>(row) || chunk.RowChangedSinceLastSystemVersion<RenderClusterGeometry>(row) || (chunk.Has<RenderMesh>() && chunk.RowChangedSinceLastSystemVersion<RenderMesh>(row));
                    foreach (var address in batches.RowsForEntity(entityIndex++))
                    {
                        _entities[address.Row] = chunk.GetEntity(row);
                        if (changed)
                            changedRows.Add(checked((uint)address.Row));
                    }
                }
        }
    }

    internal ref struct InstanceLodBounds(RenderInstanceBatches<RenderInstanceSingleGroup> batches, ReadOnlySpan<RenderMeshLodChange> changes, List<Vector4> bounds)
    {
        private readonly ReadOnlySpan<RenderMeshLodChange> _changes = changes;
        private int _entityIndex;
        internal void Collect(scoped QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                ReadOnlySpan<RenderClusterGeometry> geometries = chunk.Read<RenderClusterGeometry>();
                ReadOnlySpan<RenderTransform> transforms = chunk.Read<RenderTransform>();
                for (int row = 0; row < geometries.Length; row++, _entityIndex++)
                {
                    RenderClusterGeometry geometry = geometries[row];
                    foreach (RenderMeshLodChange change in _changes)
                    {
                        if (change.RootNode != geometry.Geometry.RootBvh)
                            continue;
                        RenderTransform transform = transforms[row];
                        Vector3 scale = transform.Stretch * transform.Scale;
                        Vector3 center = Vector3.Transform(new Vector3(change.Bounds.X, change.Bounds.Y, change.Bounds.Z) * scale, transform.Rotation) + transform.Position;
                        Vector3 absoluteScale = Vector3.Abs(scale);
                        float radius = change.Bounds.W * Math.Max(absoluteScale.X, Math.Max(absoluteScale.Y, absoluteScale.Z)) + Math.Max(geometry.BoundsExpansion, 0f);
                        foreach (var address in batches.RowsForEntity(_entityIndex))
                        {
                            bounds.Add(new Vector4(center - new Vector3(radius), address.Row));
                            bounds.Add(new Vector4(center + new Vector3(radius), address.Row));
                        }
                    }
                }
            }
        }
    }
}
