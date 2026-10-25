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
    private readonly Dictionary<Entity, Material[]> _sourceMaterials = [];
    private readonly List<Entity> _removedMaterialSources = [];
    private readonly Dictionary<Entity, SourceMesh> _sourceMeshes = [];
    private readonly HashSet<Entity> _collectedMeshSources = [];

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
        _removedMaterialSources.Clear();
        _collectedMeshSources.Clear();
    }

    public bool CompleteSourceCollection(World sourceWorld, bool fullCollection)
    {
        if (fullCollection)
        {
            foreach (Entity source in _sourceMaterials.Keys)
                if (!sourceWorld.IsAlive(source) || !sourceWorld.Has<WorldTransform>(source)
                    || !sourceWorld.Has<MeshInstance>(source) || !sourceWorld.HasBuffer<MeshMaterialBinding>(source))
                    _removedMaterialSources.Add(source);
            foreach (Entity source in _removedMaterialSources) UntrackMaterials(source);
        }
        return false;
    }

    public void CollectResources(scoped QueryCursor source, RenderExtractionResources resources)
    {
        foreach (Material[] materials in _sourceMaterials.Values)
            foreach (Material material in materials) resources.Add(material);
    }

    private Mesh TrackMesh(Entity source, in MeshInstance instance)
    {
        Mesh data = instance.Mesh.CaptureData();
        _sourceMeshes[source] = new(instance, data.Revision, GetBoundsExpansion(source, instance.BoundsExpansion));
        _collectedMeshSources.Add(source);
        return data;
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
                if (!_collectedMeshSources.Contains(source)) UntrackMesh(source);
            }
        }

        bool changed = false;
        foreach ((Entity source, SourceMesh mesh) in _sourceMeshes)
        {
            if (_collectedMeshSources.Contains(source)) continue;
            MeshInstance instance = mesh.Instance;
            float expansion = GetBoundsExpansion(source, instance.BoundsExpansion);
            if (mesh.Revision == instance.Mesh.Revision && mesh.BoundsExpansion == expansion) continue;
            Mesh data = instance.Mesh.CaptureData();
            _snapshots.Add(new MeshSnapshot(source, default, data, expansion,
                false, 0, 0, MeshChanged: true, MaterialsChanged: false, instance.PageSource,
                instance.DepthCacheClass, instance.FirstPerson));
            mesh.Revision = data.Revision;
            mesh.BoundsExpansion = expansion;
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

            _snapshots.Add(new MeshSnapshot(
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
                    TrackMaterials(entities[row], bindings);
                    for (int index = 0; index < bindings.Length; index++)
                        _materials.Add(bindings[index].Material);
                }

                bool geometryChanged = meshChanged || materialsChanged;
                MeshInstance mesh = meshes[row];
                _snapshots.Add(new MeshSnapshot(
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

    private sealed class SourceMesh(MeshInstance instance, ulong revision, float boundsExpansion)
    {
        internal readonly MeshInstance Instance = instance;
        internal ulong Revision = revision;
        internal float BoundsExpansion = boundsExpansion;
    }

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
