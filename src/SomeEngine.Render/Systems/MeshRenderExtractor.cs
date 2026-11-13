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
    private readonly List<MeshRenderValues> _meshValues = [];
    private readonly List<Material> _materials = [];
    private readonly HashSet<Entity> _meshSources = [];
    private readonly Dictionary<Entity, Material[]> _sourceMaterials = [];
    private readonly HashSet<Entity> _collectedMaterialSources = [];
    private readonly Dictionary<Entity, MeshInstance> _sourceMeshes = [];
    private readonly HashSet<Entity> _collectedMeshSources = [];

    public bool CanApplyChangesDirect =>
        _meshValues.Count == 0;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.Write<RenderMesh>();
        query.ReadOptional<WorldTransform>();
        query.ReadOptional<MeshInstance>();
        query.ReadOptionalBuffer<MeshMaterialBinding>();
    }

    public void Reset()
    {
        _meshValues.Clear();
        _materials.Clear();
        _meshSources.Clear();
        _collectedMaterialSources.Clear();
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

    private void TrackMaterials(Entity source, ReadOnlySpan<MeshMaterialBinding> bindings)
    {
        var materials = new Material[bindings.Length];
        for (int index = 0; index < bindings.Length; index++) materials[index] = bindings[index].Material;
        _sourceMaterials[source] = materials;
        _collectedMaterialSources.Add(source);
    }

    private void UntrackMaterials(Entity source) => _sourceMaterials.Remove(source);

    private float GetBoundsExpansion(Entity source, float meshExpansion)
    {
        float expansion = 0;
        if (_sourceMaterials.TryGetValue(source, out Material[]? materials))
            foreach (Material material in materials) expansion = Math.Max(expansion, material.BoundsExpansion);
        return meshExpansion + expansion;
    }

    public bool CompleteCollection(RenderExtractionContext context, bool fullCollection)
    {
        if (fullCollection)
        {
            foreach (Entity source in context.ChangedSources)
            {
                if (!_collectedMaterialSources.Contains(source)) UntrackMaterials(source);
                if (!_collectedMeshSources.Contains(source)) UntrackMesh(source);
            }
        }

        bool changed = false;
        foreach ((Entity source, MeshInstance instance) in _sourceMeshes)
        {
            if (_collectedMeshSources.Contains(source)) continue;
            Entity entity = context.RequireMirror(source);
            RenderMesh current = context.World.Read<RenderMesh>(entity);
            float expansion = GetBoundsExpansion(source, instance.BoundsExpansion);
            if (current.Mesh.Revision == instance.Mesh.Revision && current.BoundsExpansion == expansion) continue;
            Mesh data = instance.Mesh.CaptureData();
            _meshValues.Add(new MeshRenderValues(source, default, data, expansion,
                false, 0, 0, MeshChanged: true, MaterialsChanged: false, instance.PageSource,
                instance.DepthCacheClass, instance.FirstPerson));
            _meshSources.Add(source);
            changed = true;
        }
        return changed;
    }

    public void Dispose()
    {
        Reset();
        _sourceMaterials.Clear();
        _sourceMeshes.Clear();
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
            if (hasMaterialBuffer)
            {
                ReadOnlySpan<MeshMaterialBinding> bindings =
                    chunk.ReadBuffer<MeshMaterialBinding>(row).AsSpan();
                materialCount = bindings.Length;
                TrackMaterials(entities[row], bindings);
                for (int index = 0; index < bindings.Length; index++)
                    _materials.Add(bindings[index].Material);
            }

            _meshValues.Add(new MeshRenderValues(
                source,
                new RenderTransform(transforms[row].Qvvs),
                TrackMesh(source, meshes[row]),
                GetBoundsExpansion(source, meshes[row].BoundsExpansion),
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

        int firstValue = _meshValues.Count;
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
                    TrackMaterials(entities[row], bindings);
                    for (int index = 0; index < bindings.Length; index++)
                        _materials.Add(bindings[index].Material);
                }

                bool geometryChanged = meshChanged || materialsChanged;
                MeshInstance mesh = meshes[row];
                _meshValues.Add(new MeshRenderValues(
                    entities[row],
                    default,
                    geometryChanged ? TrackMesh(entities[row], mesh) : null!,
                    GetBoundsExpansion(entities[row], mesh.BoundsExpansion),
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

        return _meshValues.Count != firstValue;
    }

    public void Apply(RenderExtractionContext context)
    {
        for (int index = 0; index < _meshValues.Count; index++)
        {
            MeshRenderValues meshData = _meshValues[index];
            Entity entity = context.RetainMirror(meshData.Source);
            if (meshData.Transform is { } transform) context.RetainTransform(entity, transform);
            context.Upsert(entity, new RenderInstance());
            context.Upsert(entity, new RenderMesh(
                meshData.Mesh,
                meshData.BoundsExpansion,
                meshData.PageSource,
                meshData.DepthCacheClass,
                meshData.FirstPerson));
            if (meshData.MaterialsChanged) SyncMaterials(context, meshData, entity, allowStructuralChanges: true);
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
        for (int index = 0; index < _meshValues.Count; index++)
        {
            MeshRenderValues meshData = _meshValues[index];
            Entity entity = context.RequireMirror(meshData.Source);
            if (meshData.MeshChanged)
            {
                context.UpdateExisting(
                    entity,
                    new RenderMesh(
                        meshData.Mesh,
                        meshData.BoundsExpansion,
                        meshData.PageSource,
                        meshData.DepthCacheClass,
                        meshData.FirstPerson));
            }
            if (meshData.MaterialsChanged)
            {
                SyncMaterials(
                    context,
                    meshData,
                    entity,
                    allowStructuralChanges: false);
            }
        }
    }

    public void ValidateChanges(RenderExtractionContext context)
    {
        for (int index = 0; index < _meshValues.Count; index++)
        {
            MeshRenderValues meshData = _meshValues[index];
            Entity entity = context.RequireMirror(meshData.Source);
            if (meshData.MeshChanged)
                context.RequireExisting<RenderMesh>(entity);
            if (meshData.MaterialsChanged &&
                !context.World.HasBuffer<RenderMaterialBinding>(entity))
            {
                throw new InvalidOperationException(
                    $"Render mirror {entity} has no material buffer for changed source " +
                    $"{meshData.Source}.");
            }
        }
    }

    private void SyncMaterials(
        RenderExtractionContext context,
        in MeshRenderValues meshData,
        Entity renderEntity,
        bool allowStructuralChanges)
    {
        RenderWorld world = context.World;
        if (!meshData.HasMaterialBuffer)
        {
            if (!allowStructuralChanges)
            {
                throw new InvalidOperationException(
                    $"A changed material row for {meshData.Source} no longer has its source buffer.");
            }
            world.RemoveMaterials(renderEntity);
            return;
        }

        context.SetMaterials(renderEntity, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_materials)
            .Slice(meshData.MaterialOffset, meshData.MaterialCount));
    }

    private readonly record struct MeshRenderValues(
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
