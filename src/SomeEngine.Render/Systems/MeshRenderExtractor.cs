using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Systems;

/// <summary>Extracts mesh, transform, and material-binding semantics.</summary>
public sealed class MeshRenderExtractor : IRenderExtractionSystem
{
    private readonly List<MeshSnapshot> _snapshots = [];
    private readonly List<Material> _materials = [];
    private readonly HashSet<Entity> _meshSources = [];
    private readonly Dictionary<Entity, MeshInstance> _sourceMeshes = [];
    private readonly HashSet<Entity> _collectedMeshSources = [];
    private RenderWorld? _renderWorld;
    private QueryHandle _meshQuery;

    public bool CanApplyChangesDirect =>
        _snapshots.Count == 0;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.Write<RenderMesh>();
        query.ReadOptional<WorldTransform>();
        query.ReadOptional<MeshInstance>();
        query.ReadOptionalBuffer<MeshMaterialBinding>();
    }

    public void Reset()
    {
        _snapshots.Clear();
        _materials.Clear();
        _meshSources.Clear();
        _collectedMeshSources.Clear();
    }

    private Mesh TrackMesh(Entity source, in MeshInstance instance)
    {
        _sourceMeshes[source] = instance;
        _collectedMeshSources.Add(source);
        return instance.Mesh.CaptureData();
    }

    private void UntrackMesh(Entity source)
    {
        _sourceMeshes.Remove(source);
    }

    public bool CompleteCollection(RenderExtractionContext context, bool fullCollection)
    {
        if (fullCollection)
        {
            foreach (Entity source in context.ChangedSources)
            {
                if (!_collectedMeshSources.Contains(source)) UntrackMesh(source);
            }
        }

        if (!_meshQuery.IsValid)
        {
            _renderWorld = context.World;
            _meshQuery = _renderWorld.Query(new QueryDefinitionBuilder().Read<RenderSource>().Read<RenderMesh>());
        }
        int before = _snapshots.Count;
        var state = (Extractor: this, Context: context);
        _renderWorld!.ExecuteQuery(_meshQuery, ref state,
            static (scoped QueryCursor query, ref (MeshRenderExtractor Extractor, RenderExtractionContext Context) state) =>
                state.Extractor.CollectMeshChanges(query, state.Context));
        return _snapshots.Count != before;
    }

    private void CollectMeshChanges(scoped QueryCursor query, RenderExtractionContext context)
    {
        foreach (QueryChunkView chunk in query.Chunks)
        {
            ReadOnlySpan<RenderSource> sources = chunk.Read<RenderSource>();
            ReadOnlySpan<RenderMesh> meshes = chunk.Read<RenderMesh>();
            for (int row = 0; row < sources.Length; row++)
            {
                Entity source = sources[row].Entity;
                if (_collectedMeshSources.Contains(source) || !_sourceMeshes.TryGetValue(source, out MeshInstance instance)) continue;
                RenderMesh current = meshes[row];
                float expansion = instance.BoundsExpansion + context.ReadMaterialBoundsExpansion(source);
                if (current.Mesh.Revision == instance.Mesh.Revision && current.BoundsExpansion == expansion) continue;
                Mesh data = instance.Mesh.CaptureData();
                _snapshots.Add(new MeshSnapshot(source, default, data, expansion,
                    false, 0, 0, MeshChanged: true, MaterialsChanged: false, instance.PageSource,
                    instance.DepthCacheClass, instance.FirstPerson));
                _meshSources.Add(source);
            }
        }
    }

    public void Dispose()
    {
        Reset();
        _sourceMeshes.Clear();
        if (_meshQuery.IsValid)
        {
            _renderWorld!.ReleaseQuery(_meshQuery);
            _meshQuery = default;
            _renderWorld = null;
        }
    }

    public void Collect(QueryChunkView chunk)
    {
        if (!chunk.TryRead<WorldTransform>(out ReadOnlySpan<WorldTransform> transforms))
            return;

        if (!chunk.TryRead<MeshInstance>(out ReadOnlySpan<MeshInstance> meshes))
            return;

        ReadOnlySpan<Entity> entities = chunk.Entities;
        bool hasMaterialBuffer = chunk.HasBuffer<MeshMaterialBinding>();
        for (int row = 0; row < entities.Length; row++)
        {
            Entity source = entities[row];
            int materialOffset = _materials.Count;
            int materialCount = 0;
            float materialExpansion = 0;
            if (hasMaterialBuffer)
            {
                ReadOnlySpan<MeshMaterialBinding> bindings =
                    chunk.ReadBuffer<MeshMaterialBinding>(row).AsSpan();
                materialCount = bindings.Length;
                for (int index = 0; index < bindings.Length; index++)
                {
                    _materials.Add(bindings[index].Material);
                    materialExpansion = Math.Max(materialExpansion, bindings[index].Material.BoundsExpansion);
                }
            }

            _snapshots.Add(new MeshSnapshot(
                source,
                new RenderTransform(transforms[row].Qvvs),
                TrackMesh(source, meshes[row]),
                meshes[row].BoundsExpansion + materialExpansion,
                hasMaterialBuffer,
                materialOffset,
                materialCount,
                MeshChanged: true,
                MaterialsChanged: true,
                meshes[row].PageSource,
                meshes[row].DepthCacheClass,
                meshes[row].FirstPerson));
            _meshSources.Add(source);
        }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (!chunk.Has<WorldTransform>())
            return false;

        if (!chunk.TryRead<MeshInstance>(out ReadOnlySpan<MeshInstance> meshes))
            return false;

        if (chunk.HasChangedSinceLastSystemVersion<WorldTransform>())
            context.CollectTransformChanges(chunk);

        bool meshChunkChanged = chunk.HasChangedSinceLastSystemVersion<MeshInstance>();
        bool hasMaterialBuffer = chunk.HasBuffer<MeshMaterialBinding>();
        bool materialChunkChanged =
            hasMaterialBuffer &&
            chunk.HasBufferChangedSinceLastSystemVersion<MeshMaterialBinding>();
        if (!meshChunkChanged && !materialChunkChanged)
            return false;

        int firstSnapshot = _snapshots.Count;
        ReadOnlySpan<Entity> entities = chunk.Entities;
        for (int row = 0; row < entities.Length; row++)
        {
            bool meshChanged =
                meshChunkChanged &&
                chunk.RowChangedSinceLastSystemVersion<MeshInstance>(row);
            bool materialsChanged =
                materialChunkChanged &&
                chunk.RowBufferChangedSinceLastSystemVersion<MeshMaterialBinding>(row);
            if (meshChanged || materialsChanged)
            {
                int materialOffset = _materials.Count;
                int materialCount = 0;
                if (materialsChanged)
                {
                    ReadOnlySpan<MeshMaterialBinding> bindings =
                        chunk.ReadBuffer<MeshMaterialBinding>(row).AsSpan();
                    materialCount = bindings.Length;
                    for (int index = 0; index < bindings.Length; index++)
                        _materials.Add(bindings[index].Material);
                }

                bool geometryChanged = meshChanged || materialsChanged;
                MeshInstance mesh = meshes[row];
                _snapshots.Add(new MeshSnapshot(
                    entities[row],
                    default,
                    geometryChanged ? TrackMesh(entities[row], mesh) : null!,
                    mesh.BoundsExpansion + context.ReadMaterialBoundsExpansion(entities[row]),
                    hasMaterialBuffer,
                    materialOffset,
                    materialCount,
                    geometryChanged,
                    materialsChanged,
                    mesh.PageSource,
                    mesh.DepthCacheClass,
                    mesh.FirstPerson));
            }
        }

        return _snapshots.Count != firstSnapshot;
    }

    public void Apply(RenderExtractionContext context)
    {
        for (int index = 0; index < _snapshots.Count; index++)
        {
            MeshSnapshot snapshot = _snapshots[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            if (snapshot.Transform is { } transform) context.RetainTransform(entity, transform);
            context.Upsert(entity, new RenderInstance());
            context.Upsert(entity, new RenderMesh(
                snapshot.Mesh,
                snapshot.BoundsExpansion,
                snapshot.PageSource,
                snapshot.DepthCacheClass,
                snapshot.FirstPerson));
            if (snapshot.MaterialsChanged) SyncMaterials(context, snapshot, entity, allowStructuralChanges: true);
        }

        ReadOnlySpan<RenderMirror> mirrors = context.Mirrors;
        for (int index = 0; index < mirrors.Length; index++)
        {
            RenderMirror mirror = mirrors[index];
            bool retainedMesh = _meshSources.Contains(mirror.Source);
            if (!retainedMesh && context.World.Has<RenderMesh>(mirror.RenderEntity))
            {
                context.RemoveIfExists<RenderInstance>(mirror.RenderEntity);
                context.RemoveIfExists<RenderMesh>(mirror.RenderEntity);
                context.World.RemoveMaterials(mirror.RenderEntity);
            }
        }
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        for (int index = 0; index < _snapshots.Count; index++)
        {
            MeshSnapshot snapshot = _snapshots[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.MeshChanged)
            {
                context.UpdateExisting(
                    entity,
                    new RenderMesh(
                        snapshot.Mesh,
                        snapshot.BoundsExpansion,
                        snapshot.PageSource,
                        snapshot.DepthCacheClass,
                        snapshot.FirstPerson));
            }
            if (snapshot.MaterialsChanged)
            {
                SyncMaterials(
                    context,
                    snapshot,
                    entity,
                    allowStructuralChanges: false);
            }
        }
    }

    public void ValidateChanges(RenderExtractionContext context)
    {
        for (int index = 0; index < _snapshots.Count; index++)
        {
            MeshSnapshot snapshot = _snapshots[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.MeshChanged)
                context.RequireExisting<RenderMesh>(entity);
            if (snapshot.MaterialsChanged &&
                !context.World.HasBuffer<RenderMaterialBinding>(entity))
            {
                throw new InvalidOperationException(
                    $"Render mirror {entity} has no material buffer for changed source " +
                    $"{snapshot.Source}.");
            }
        }
    }

    private void SyncMaterials(
        RenderExtractionContext context,
        in MeshSnapshot snapshot,
        Entity renderEntity,
        bool allowStructuralChanges)
    {
        RenderWorld world = context.World;
        if (!snapshot.HasMaterialBuffer)
        {
            if (!allowStructuralChanges)
            {
                throw new InvalidOperationException(
                    $"A changed material row for {snapshot.Source} no longer has its source buffer.");
            }
            world.RemoveMaterials(renderEntity);
            return;
        }

        context.SetMaterials(renderEntity, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_materials)
            .Slice(snapshot.MaterialOffset, snapshot.MaterialCount));
    }

    private readonly record struct MeshSnapshot(
        Entity Source,
        RenderTransform? Transform,
        Mesh Mesh,
        float BoundsExpansion,
        bool HasMaterialBuffer,
        int MaterialOffset,
        int MaterialCount,
        bool MeshChanged,
        bool MaterialsChanged,
        SomeEngine.Assets.Pipeline.MeshPayloadSource? PageSource,
        DepthCacheClass DepthCacheClass,
        FirstPersonMeshData FirstPerson);

    private readonly record struct MaterialSlice(
        List<Material> Values,
        int Offset,
        int Count);

    private struct MaterialSliceComparison(
        List<Material> values,
        int offset,
        int count)
    {
        internal List<Material> Values = values;
        internal int Offset = offset;
        internal int Count = count;
        internal bool Equal = true;
    }
}
