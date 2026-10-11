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
        internal void Collect(QueryCursor query)
        {
            bool meshChanged = membershipChanged;
            if (!meshChanged)
                foreach (QueryChunkView chunk in query.Chunks)
                    if (chunk.HasChangedSinceLastSystemVersion<RenderMesh>())
                    {
                        meshChanged = true;
                        break;
                    }
            if (meshChanged)
            {
                if (pipeline._casterCacheClasses.Length != instances.AddressCount)
                    pipeline._casterCacheClasses = new DepthCacheClass[instances.AddressCount];
                bool firstPerson = false;
                int entityIndex = 0;
                foreach (QueryChunkView chunk in query.Chunks)
                    foreach (RenderMesh mesh in chunk.Read<RenderMesh>())
                    {
                        firstPerson |= mesh.FirstPerson.Kind == FirstPersonMeshKind.WorldRepresentation;
                        foreach (var address in instances.RowsForEntity(entityIndex++))
                            pipeline._casterCacheClasses[address.Row] = mesh.DepthCacheClass;
                    }
                pipeline._hasFirstPersonWorldMeshes = firstPerson;
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

    private void RecordMaterialCasterChanges(ClusterMaterialAssignmentSet assignments)
    {
        if (!assignments.ChangedMaterialIds.Any() && !assignments.ChangedOffsets.Any()) return;
        var entities = new HashSet<Entity>();
        assignments.CollectChangedEntities(entities);
        RecordCasterChange([], entities);
    }

    private void RecordTextureCasterChanges(HashSet<Texture> changed)
    {
        if (_installedRasterStages is null) return;
        var materials = new HashSet<long>();
        var textures = new HashSet<Texture>();
        foreach (ClusterRasterInvocation raster in _installedRasterStages.Invocations)
        {
            textures.Clear();
            raster.CommandBindings.CollectTextures(textures);
            if (textures.Overlaps(changed)) materials.Add(raster.Material.MaterialId);
        }
        var entities = new HashSet<Entity>();
        _materialAssignments.Current.CollectMaterialEntities(materials, entities);
        RecordCasterChange([], entities);
    }

    /// <summary>Collects current instance rows affected by uploaded geometry and extracted raster inputs.</summary>
    public bool CollectChangedCasters(ulong afterRevision, List<uint> rows,
        List<uint> streamingRows, out ulong revision)
    {
        if (_resources.Residency.GetBinding().Publication is { } publication &&
            publication.Sequence != _lastGeometryChangeSequence)
        {
            var lodMeshes = _resources.LodChanges.Span.ToArray().Select(change => change.Mesh).ToHashSet();
            RecordCasterChange(publication.Meshes.Bvh.ChangedMeshes.Where(mesh => !lodMeshes.Contains(mesh)).ToArray(), []);
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
        internal void Collect(QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                bool materialChanged = entities.Contains(row.Entity);
                bool geometryChanged = meshes.Contains(row.Read<RenderMesh>().Mesh);
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
            var inputs = new CasterLodBounds(_instances.Current!, _resources.Residency, changes.Span, lodBounds);
            _resources.World.ExecuteQuery(_sceneQuery, ref inputs,
                static (QueryCursor query, ref CasterLodBounds data) => data.Collect(query));
        }
        return _casterCacheClasses;
    }

    /// <summary>Maps rebuilt caster rows to entities and reports actual transform or mesh writes.</summary>
    public void CollectCasterEntities(Span<Entity> entities, uint afterVersion, List<uint> changedRows)
    {
        var inputs = new CasterEntities(_instances.Current!, entities, changedRows);
        _resources.World.ExecuteQuery(_sceneQuery, afterVersion, ref inputs,
            static (QueryCursor query, ref CasterEntities data) => data.Collect(query));
    }

    private ref struct CasterEntities(RenderInstanceBatches<RenderInstanceSingleGroup> batches,
        Span<Entity> entities, List<uint> changedRows)
    {
        private readonly Span<Entity> _entities = entities;
        internal void Collect(QueryCursor query)
        {
            int entityIndex = 0;
            foreach (QueryChunkView chunk in query.Chunks)
                for (int row = 0; row < chunk.Count; row++)
                {
                    bool changed = chunk.RowChangedSinceLastSystemVersion<RenderTransform>(row) ||
                        chunk.RowChangedSinceLastSystemVersion<RenderMesh>(row);
                    foreach (var address in batches.RowsForEntity(entityIndex++))
                    {
                        _entities[address.Row] = chunk.GetEntity(row);
                        if (changed) changedRows.Add(checked((uint)address.Row));
                    }
                }
        }
    }

    private ref struct CasterLodBounds(RenderInstanceBatches<RenderInstanceSingleGroup> batches,
        ClusterResidency residency, ReadOnlySpan<ClusterLodChange> changes, List<Vector4> bounds)
    {
        private readonly ReadOnlySpan<ClusterLodChange> _changes = changes;
        private int _entityIndex;
        internal void Collect(QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                ReadOnlySpan<RenderMesh> meshes = chunk.Read<RenderMesh>();
                ReadOnlySpan<RenderTransform> transforms = chunk.Read<RenderTransform>();
                for (int row = 0; row < meshes.Length; row++, _entityIndex++)
                {
                    RenderMesh mesh = meshes[row];
                    if (!residency.TryGetPublishedRoot(mesh.Mesh, out uint root)) continue;
                    foreach (ClusterLodChange change in _changes)
                    {
                        if (change.RootNode != root || !ReferenceEquals(change.Mesh, mesh.Mesh)) continue;
                        RenderTransform transform = transforms[row];
                        Vector3 scale = transform.Stretch * transform.Scale;
                        Vector3 center = Vector3.Transform(new Vector3(change.Bounds.X, change.Bounds.Y, change.Bounds.Z) * scale,
                            transform.Rotation) + transform.Position;
                        Vector3 absoluteScale = Vector3.Abs(scale);
                        float radius = change.Bounds.W * Math.Max(absoluteScale.X, Math.Max(absoluteScale.Y, absoluteScale.Z)) +
                            Math.Max(mesh.BoundsExpansion, 0f);
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
