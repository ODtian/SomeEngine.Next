using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;
using System.Numerics;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private DepthCacheClass[] _casterCacheClasses = [];
    private RenderInstanceBatches<RenderInstanceSingleGroup>? _casterInputInstances;
    private uint _casterInputVersion;
    private bool _hasFirstPersonWorldMeshes;
    private bool _hasFirstPersonReceiverMeshes;

    /// <summary>Reports world-space first-person representations in the current render entities.</summary>
    public bool HasFirstPersonWorldMeshes()
    {
        RefreshCasterInputs();
        return _hasFirstPersonWorldMeshes;
    }

    private void RefreshCasterInputs()
    {
        RenderInstanceBatches<RenderInstanceSingleGroup> instances = _instances.Current!;
        bool membershipChanged = !ReferenceEquals(instances, _casterInputInstances);
        if (!membershipChanged && _casterInputVersion == _resources.World.CurrentTick) return;
        var inputs = new CasterInputChanges(this, instances, membershipChanged);
        _resources.World.ExecuteQuery(_sceneQuery, _casterInputVersion, ref inputs,
            static (QueryCursor query, ref CasterInputChanges data) => data.Collect(query));
    }

    private readonly struct CasterInputChanges(ClusterPipelineSystem pipeline,
        RenderInstanceBatches<RenderInstanceSingleGroup> instances, bool membershipChanged)
    {
        internal void Collect(scoped QueryCursor query)
        {
            bool meshChanged = membershipChanged;
            if (!meshChanged)
                foreach (QueryChunkView chunk in query.Chunks)
                    if (chunk.HasChangedSinceLastSystemVersion<RenderClusterGeometry>() ||
                        (chunk.Has<RenderMesh>() && chunk.HasChangedSinceLastSystemVersion<RenderMesh>()))
                    {
                        meshChanged = true;
                        break;
                    }
            if (meshChanged)
            {
                if (pipeline._casterCacheClasses.Length != instances.AddressCount)
                    pipeline._casterCacheClasses = new DepthCacheClass[instances.AddressCount];
                bool firstPerson = false;
                bool firstPersonReceiver = false;
                int entityIndex = 0;
                foreach (QueryChunkView chunk in query.Chunks)
                {
                    bool hasMesh = chunk.TryRead<RenderMesh>(out ReadOnlySpan<RenderMesh> meshes);
                    for (int row = 0; row < chunk.Count; row++)
                    {
                        RenderMesh mesh = hasMesh ? meshes[row] : default;
                        firstPerson |= hasMesh && mesh.FirstPerson.Kind == FirstPersonMeshKind.WorldRepresentation;
                        firstPersonReceiver |= hasMesh && mesh.FirstPerson.Kind == FirstPersonMeshKind.FirstPerson;
                        foreach (var address in instances.RowsForEntity(entityIndex++))
                            pipeline._casterCacheClasses[address.Row] = hasMesh ? mesh.DepthCacheClass : DepthCacheClass.Auto;
                    }
                }
                pipeline._hasFirstPersonWorldMeshes = firstPerson;
                pipeline._hasFirstPersonReceiverMeshes = firstPersonReceiver;
                pipeline._casterInputInstances = instances;
            }
            pipeline._casterInputVersion = query.CurrentSystemVersion;
        }
    }

    private sealed record CasterChange(ulong Revision, Mesh[] Meshes, HashSet<Entity> Entities, bool All);
    private readonly List<CasterChange> _casterChanges = [];
    private ulong _casterRevision;
    private ulong _lastGeometryChangeSequence;
    private float _casterStreamingQuality = 1.0f;

    private void RecordCasterChange(Mesh[] meshes, HashSet<Entity> entities, bool all = false)
    {
        if (meshes.Length == 0 && entities.Count == 0 && !all) return;
        _casterRevision = checked(_casterRevision + 1);
        _casterChanges.Add(new(_casterRevision, meshes, entities, all));
        if (_casterChanges.Count > 64) _casterChanges.RemoveAt(0);
    }

    private readonly HashSet<long> _rasterChangedMaterials = [];

    private void RecordMaterialCasterChanges(ClusterMaterialAssignmentSet assignments)
    {
        if (_rasterChangedMaterials.Count == 0 && !assignments.ChangedOffsets.Any()) return;
        var entities = new HashSet<Entity>();
        assignments.CollectChangedEntities(entities, _rasterChangedMaterials);
        RecordCasterChange([], entities);
    }

    private bool RecordTextureCasterChanges(HashSet<Texture> changed)
    {
        if (_installedRasterStages is null) return false;
        var materials = new HashSet<long>();
        // Direct bindings and indexed scalar textures share the raster stage's
        // resolved depth dependencies. Undeclared evaluators consume every texture.
        foreach (Material material in _materialAssignments.Current.DistinctMaterials)
        {
            if (_rasterStages!.TryGetMaterialDepthInputs(material.MaterialId, out var properties))
            {
                foreach (uint property in properties)
                    if (material.GetTexture(property) is { } texture && changed.Contains(texture))
                    {
                        materials.Add(material.MaterialId);
                        break;
                    }
            }
            else
            {
                for (uint property = 0; property < material.PropertyCount; property++)
                    if (material.GetTexture(property) is { } texture && changed.Contains(texture))
                    {
                        materials.Add(material.MaterialId);
                        break;
                    }
            }
        }
        if (materials.Count == 0) return false;
        var entities = new HashSet<Entity>();
        _materialAssignments.Current.CollectMaterialEntities(materials, entities);
        RecordCasterChange([], entities);
        return true;
    }

    /// <summary>Collects current instance rows affected by uploaded geometry and extracted raster inputs.</summary>
    public bool CollectChangedCasters(ulong afterRevision, List<uint> rows,
        List<uint> streamingRows, out ulong revision)
    {
        if (_resources.Residency.CurrentChanges is { } publication &&
            publication.Sequence != _lastGeometryChangeSequence)
        {
            Mesh[] changedMeshes = publication.Meshes.Bvh.ChangedMeshes;
            ReadOnlySpan<ClusterLodChange> lodChanges = publication.Meshes.LodChanges;
            if (changedMeshes.Length != 0 && !lodChanges.IsEmpty)
            {
                var lodMeshes = new HashSet<Mesh>();
                foreach (ref readonly ClusterLodChange change in lodChanges)
                    lodMeshes.Add(change.Mesh);
                changedMeshes = changedMeshes.Where(mesh => !lodMeshes.Contains(mesh)).ToArray();
            }
            if (changedMeshes.Length != 0)
                RecordCasterChange(changedMeshes, []);
            _lastGeometryChangeSequence = publication.Sequence;
        }
        float quality = _resources.Residency.StreamingQuality;
        if (quality != _casterStreamingQuality)
        {
            RecordCasterChange([], [], all: true);
            _casterStreamingQuality = quality;
        }
        revision = _casterRevision;
        if (afterRevision == _casterRevision) return true;
        if (_casterChanges.Count == 0 || afterRevision + 1 < _casterChanges[0].Revision) return false;
        var meshes = new HashSet<Mesh>();
        var entities = new HashSet<Entity>();
        bool all = false;
        foreach (CasterChange change in _casterChanges)
        {
            if (change.Revision <= afterRevision) continue;
            meshes.UnionWith(change.Meshes);
            entities.UnionWith(change.Entities);
            all |= change.All;
        }
        var changed = new ChangedCasters(_instances.Current!, meshes, entities, rows, streamingRows, all);
        _resources.World.ExecuteQuery(_sceneQuery, ref changed,
            static (QueryCursor query, ref ChangedCasters data) => data.Collect(query));
        return true;
    }

    private struct ChangedCasters(RenderInstanceBatches<RenderInstanceSingleGroup> batches,
        HashSet<Mesh> meshes, HashSet<Entity> entities, List<uint> rows, List<uint> streamingRows, bool all)
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

    /// <summary>Reads caller cache choices and collects world-space bounds only for changed LOD groups.</summary>
    public ReadOnlySpan<DepthCacheClass> CollectCasterCacheInputs(List<Vector4> lodBounds) =>
        CollectCasterCacheInputs(lodBounds, out _);

    /// <summary>Reads cache choices and the render-world version observed with them.</summary>
    public ReadOnlySpan<DepthCacheClass> CollectCasterCacheInputs(List<Vector4> lodBounds, out uint version)
    {
        RefreshCasterInputs();
        version = _casterInputVersion;
        ReadOnlyMemory<ClusterLodChange> changes = _resources.LodChanges;
        if (!changes.IsEmpty)
        {
            ReadOnlySpan<ClusterLodChange> lodChanges = changes.Span;
            var firstChanges = new Dictionary<uint, int>(lodChanges.Length);
            var nextChanges = new int[lodChanges.Length];
            // Link each published root's changes in order without querying old mesh ownership.
            for (int index = lodChanges.Length - 1; index >= 0; index--)
            {
                uint root = lodChanges[index].RootNode;
                nextChanges[index] = firstChanges.GetValueOrDefault(root, -1);
                firstChanges[root] = index;
            }
            var inputs = new CasterLodBounds(_instances.Current!, lodChanges,
                firstChanges, nextChanges, lodBounds);
            _resources.World.ExecuteQuery(_sceneQuery, ref inputs,
                static (scoped QueryCursor query, ref CasterLodBounds data) => data.Collect(query));
        }
        return _casterCacheClasses;
    }

    /// <summary>Maps rebuilt caster rows to entities and reports actual transform or mesh writes.</summary>
    public void CollectCasterEntities(Span<Entity> entities, uint afterVersion, List<uint> changedRows)
    {
        var inputs = new CasterEntities(_instances.Current!, entities, changedRows);
        _resources.World.ExecuteQuery(_sceneQuery, afterVersion, ref inputs,
            static (scoped QueryCursor query, ref CasterEntities data) => data.Collect(query));
    }

    private ref struct CasterEntities(RenderInstanceBatches<RenderInstanceSingleGroup> batches,
        Span<Entity> entities, List<uint> changedRows)
    {
        private readonly Span<Entity> _entities = entities;
        internal void Collect(scoped QueryCursor query)
        {
            int entityIndex = 0;
            foreach (QueryChunkView chunk in query.Chunks)
                for (int row = 0; row < chunk.Count; row++)
                {
                    bool changed = chunk.RowChangedSinceLastSystemVersion<RenderTransform>(row) ||
                        chunk.RowChangedSinceLastSystemVersion<RenderClusterGeometry>(row) ||
                        (chunk.Has<RenderMesh>() && chunk.RowChangedSinceLastSystemVersion<RenderMesh>(row));
                    foreach (var address in batches.RowsForEntity(entityIndex++))
                    {
                        _entities[address.Row] = chunk.GetEntity(row);
                        if (changed) changedRows.Add(checked((uint)address.Row));
                    }
                }
        }
    }

    private ref struct CasterLodBounds(RenderInstanceBatches<RenderInstanceSingleGroup> batches,
        ReadOnlySpan<ClusterLodChange> changes, Dictionary<uint, int> firstChanges,
        ReadOnlySpan<int> nextChanges, List<Vector4> bounds)
    {
        private readonly ReadOnlySpan<ClusterLodChange> _changes = changes;
        private readonly ReadOnlySpan<int> _nextChanges = nextChanges;
        private int _entityIndex;
        internal void Collect(scoped QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                ReadOnlySpan<RenderClusterGeometry> geometries = chunk.Read<RenderClusterGeometry>();
                bool hasMeshes = chunk.TryRead<RenderMesh>(out ReadOnlySpan<RenderMesh> meshes);
                ReadOnlySpan<RenderTransform> transforms = chunk.Read<RenderTransform>();
                for (int row = 0; row < geometries.Length; row++, _entityIndex++)
                {
                    RenderClusterGeometry geometry = geometries[row];
                    if (!firstChanges.TryGetValue(geometry.Geometry.RootBvh, out int firstChange)) continue;
                    for (int index = firstChange; index >= 0; index = _nextChanges[index])
                    {
                        ClusterLodChange change = _changes[index];
                        if (hasMeshes && !ReferenceEquals(change.Mesh, meshes[row].Mesh)) continue;
                        RenderTransform transform = transforms[row];
                        Vector3 scale = transform.Stretch * transform.Scale;
                        Vector3 center = Vector3.Transform(new Vector3(change.Bounds.X, change.Bounds.Y, change.Bounds.Z) * scale,
                            transform.Rotation) + transform.Position;
                        Vector3 absoluteScale = Vector3.Abs(scale);
                        float radius = change.Bounds.W * Math.Max(absoluteScale.X, Math.Max(absoluteScale.Y, absoluteScale.Z)) +
                            Math.Max(geometry.BoundsExpansion, 0f);
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
