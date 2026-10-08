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
    /// <summary>Reports world-space first-person representations in the current render entities.</summary>
    public bool HasFirstPersonWorldMeshes()
    {
        bool found = false;
        _resources.World.ExecuteQuery(_sceneQuery, ref found,
            static (QueryCursor query, ref bool value) =>
            {
                foreach (QueryRow row in query.Rows)
                    value |= row.Read<RenderMesh>().FirstPerson.Kind == FirstPersonMeshKind.WorldRepresentation;
            });
        return found;
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

    /// <summary>Copies caller cache choices and the current world-space bounds of changed LOD groups.</summary>
    public void CollectCasterCacheInputs(DepthCacheClass[] classes, List<Vector4> lodBounds)
    {
        var inputs = new CasterCacheInputs(_instances.Current!, _resources.Residency, classes, _resources.LodChanges.ToArray(), lodBounds);
        _resources.World.ExecuteQuery(_sceneQuery, ref inputs,
            static (QueryCursor query, ref CasterCacheInputs data) => data.Collect(query));
    }

    private struct CasterCacheInputs(RenderInstanceBatches<RenderInstanceSingleGroup> batches,
        ClusterResidency residency, DepthCacheClass[] classes, ClusterLodChange[] changes, List<Vector4> bounds)
    {
        private int _entityIndex;
        internal void Collect(QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                RenderMesh mesh = row.Read<RenderMesh>();
                RenderTransform transform = row.Read<RenderTransform>();
                uint root = 0;
                bool hasRoot = changes.Length != 0 && residency.TryGetPublishedRoot(mesh.Mesh, out root);
                foreach (var address in batches.RowsForEntity(_entityIndex))
                {
                    classes[address.Row] = mesh.DepthCacheClass;
                    foreach (ClusterLodChange change in changes)
                    {
                        if (!hasRoot || change.RootNode != root || !ReferenceEquals(change.Mesh, mesh.Mesh)) continue;
                        Vector3 scale = transform.Stretch * transform.Scale;
                        Vector3 center = Vector3.Transform(new Vector3(change.Bounds.X, change.Bounds.Y, change.Bounds.Z) * scale,
                            transform.Rotation) + transform.Position;
                        Vector3 absoluteScale = Vector3.Abs(scale);
                        float radius = change.Bounds.W * Math.Max(absoluteScale.X, Math.Max(absoluteScale.Y, absoluteScale.Z)) +
                            Math.Max(mesh.BoundsExpansion, 0f);
                        bounds.Add(new Vector4(center - new Vector3(radius), address.Row));
                        bounds.Add(new Vector4(center + new Vector3(radius), address.Row));
                    }
                }
                _entityIndex++;
            }
        }
    }
}
