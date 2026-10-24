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
    private readonly Dictionary<Material, HashSet<Entity>> _materialSources = [];
    private readonly HashSet<Material> _addedMaterials = [];
    private readonly HashSet<long> _removedMaterials = [];
    private readonly HashSet<Entity> _collectedMaterialSources = [];
    private readonly object _assetChangesGate = new();
    private readonly HashSet<Material> _referencedMaterials = [];
    private readonly Dictionary<Entity, MeshInstance> _sourceMeshes = [];
    private readonly Dictionary<Mesh, HashSet<Entity>> _meshAssetSources = [];
    private readonly HashSet<Entity> _changedMeshSources = [];
    private readonly HashSet<Entity> _collectedMeshSources = [];

    public bool CanApplyChangesDirect =>
        _snapshots.Count == 0 && _removedMaterials.Count == 0;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.Write<RenderMaterialReference>();
        query.ReadDestination<RenderMaterialIndex>();
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
        _collectedMaterialSources.Clear();
        _referencedMaterials.Clear();
        _collectedMeshSources.Clear();
    }

    private Material CollectMaterial(Material material)
    {
        _referencedMaterials.Add(material);
        return material;
    }

    private Mesh TrackMesh(Entity source, in MeshInstance instance)
    {
        lock (_assetChangesGate)
        {
            if (_sourceMeshes.TryGetValue(source, out MeshInstance previous) && !ReferenceEquals(previous.Mesh, instance.Mesh))
                UntrackMesh(source);
            _sourceMeshes[source] = instance;
            if (!_meshAssetSources.TryGetValue(instance.Mesh, out var sources))
            {
                _meshAssetSources.Add(instance.Mesh, sources = []);
                instance.Mesh.Changed += CollectMeshChange;
            }
            sources.Add(source);
            _changedMeshSources.Add(source);
            _collectedMeshSources.Add(source);
            return instance.Mesh.CaptureData();
        }
    }

    private void UntrackMesh(Entity source)
    {
        if (!_sourceMeshes.Remove(source, out MeshInstance instance)) return;
        HashSet<Entity> sources = _meshAssetSources[instance.Mesh];
        sources.Remove(source);
        if (sources.Count == 0)
        {
            instance.Mesh.Changed -= CollectMeshChange;
            _meshAssetSources.Remove(instance.Mesh);
        }
        _changedMeshSources.Remove(source);
    }

    private void CollectMeshChange(Mesh mesh)
    {
        lock (_assetChangesGate)
            if (_meshAssetSources.TryGetValue(mesh, out var sources))
                _changedMeshSources.UnionWith(sources);
    }

    private void TrackMaterials(Entity source, ReadOnlySpan<MeshMaterialBinding> bindings)
    {
        lock (_assetChangesGate)
        {
            UntrackMaterials(source);
            var materials = new Material[bindings.Length];
            for (int index = 0; index < bindings.Length; index++)
            {
                Material material = bindings[index].Material;
                materials[index] = material;
                if (!_materialSources.TryGetValue(material, out var sources))
                {
                    _materialSources.Add(material, sources = []);
                    _addedMaterials.Add(material);
                    _removedMaterials.Remove(material.MaterialId);
                }
                sources.Add(source);
            }
            _sourceMaterials.Add(source, materials);
            _collectedMaterialSources.Add(source);
        }
    }

    private void UntrackMaterials(Entity source)
    {
        if (!_sourceMaterials.Remove(source, out Material[]? materials)) return;
        foreach (Material material in materials)
        {
            if (!_materialSources.TryGetValue(material, out var sources)) continue;
            sources.Remove(source);
            if (sources.Count != 0) continue;
            _materialSources.Remove(material);
            _addedMaterials.Remove(material);
            _removedMaterials.Add(material.MaterialId);
        }
    }

    public bool CompleteCollection(RenderExtractionContext context, bool fullCollection)
    {
        lock (_assetChangesGate)
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
            foreach (Entity source in _changedMeshSources.ToArray())
            {
                if (_collectedMeshSources.Contains(source)) continue;
                MeshInstance instance = _sourceMeshes[source];
                Mesh data = instance.Mesh.CaptureData();
                Entity entity = context.RequireMirror(source);
                if (ReferenceEquals(context.World.Read<RenderMesh>(entity).Mesh, data))
                {
                    _changedMeshSources.Remove(source);
                    continue;
                }
                _snapshots.Add(new MeshSnapshot(source, default, data, instance.BoundsExpansion,
                    false, 0, 0, MeshChanged: true, MaterialsChanged: false, instance.PageSource));
                _meshSources.Add(source);
                changed = true;
            }
            foreach (Material material in _addedMaterials.ToArray())
            {
                Material data = CollectMaterial(material);
                if (context.World.TryReadMaterial(data.MaterialId, out Material? current) &&
                    ReferenceEquals(current, data))
                    _addedMaterials.Remove(material);
                else
                    changed = true;
            }
            foreach (long id in _removedMaterials.ToArray())
                if (context.World.TryReadMaterial(id, out _)) changed = true;
                else _removedMaterials.Remove(id);
            return changed;
        }
    }

    public void Dispose()
    {
        lock (_assetChangesGate)
        {
            Reset();
            _materialSources.Clear();
            _sourceMaterials.Clear();
            _addedMaterials.Clear();
            _removedMaterials.Clear();
            foreach (Mesh mesh in _meshAssetSources.Keys) mesh.Changed -= CollectMeshChange;
            _meshAssetSources.Clear();
            _sourceMeshes.Clear();
            _changedMeshSources.Clear();
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
            if (hasMaterialBuffer)
            {
                ReadOnlySpan<MeshMaterialBinding> bindings =
                    chunk.ReadBuffer<MeshMaterialBinding>(row).AsSpan();
                materialCount = bindings.Length;
                TrackMaterials(entities[row], bindings);
                for (int index = 0; index < bindings.Length; index++)
                    _materials.Add(CollectMaterial(bindings[index].Material));
            }

            _snapshots.Add(new MeshSnapshot(
                source,
                new RenderTransform(transforms[row].Qvvs),
                TrackMesh(source, meshes[row]),
                meshes[row].BoundsExpansion,
                hasMaterialBuffer,
                materialOffset,
                materialCount,
                MeshChanged: true,
                MaterialsChanged: true,
                meshes[row].PageSource));
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
                        _materials.Add(CollectMaterial(bindings[index].Material));
                }

                MeshInstance mesh = meshes[row];
                _snapshots.Add(new MeshSnapshot(
                    entities[row],
                    default,
                    meshChanged ? TrackMesh(entities[row], mesh) : null!,
                    mesh.BoundsExpansion,
                    hasMaterialBuffer,
                    materialOffset,
                    materialCount,
                    meshChanged,
                    materialsChanged,
                    mesh.PageSource));
            }
        }

        return _snapshots.Count != firstSnapshot;
    }

    public void Apply(RenderExtractionContext context)
    {
        context.World.UpdateMaterials(_referencedMaterials, _removedMaterials);
        for (int index = 0; index < _snapshots.Count; index++)
        {
            MeshSnapshot snapshot = _snapshots[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            if (snapshot.Transform is { } transform) context.RetainTransform(entity, transform);
            context.Upsert(entity, new RenderInstance());
            context.Upsert(entity, new RenderMesh(
                snapshot.Mesh,
                snapshot.BoundsExpansion,
                snapshot.PageSource));
            SyncMaterials(context.World, snapshot, entity, allowStructuralChanges: true);
        }

        ReadOnlySpan<RenderMirror> mirrors = context.Mirrors;
        for (int index = 0; index < mirrors.Length; index++)
        {
            RenderMirror mirror = mirrors[index];
            bool retainedMesh = _meshSources.Contains(mirror.Source);
            if (!retainedMesh)
            {
                context.RemoveIfExists<RenderInstance>(mirror.RenderEntity);
                context.RemoveIfExists<RenderMesh>(mirror.RenderEntity);
                if (context.World.HasBuffer<RenderMaterialBinding>(mirror.RenderEntity))
                    context.World.RemoveBuffer<RenderMaterialBinding>(mirror.RenderEntity);
            }
        }
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        context.World.UpdateMaterials(_referencedMaterials, _removedMaterials);
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
                        snapshot.PageSource));
            }
            if (snapshot.MaterialsChanged)
            {
                SyncMaterials(
                    context.World,
                    snapshot,
                    entity,
                    allowStructuralChanges: false);
            }
        }
    }

    public void ValidateChanges(RenderExtractionContext context)
    {
        foreach (Material data in _referencedMaterials)
            if (!context.World.TryReadMaterial(data.MaterialId, out _))
                throw new InvalidOperationException($"The rendering world has no material {data.MaterialId} to update.");
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
        RenderWorld world,
        in MeshSnapshot snapshot,
        Entity renderEntity,
        bool allowStructuralChanges)
    {
        if (!snapshot.HasMaterialBuffer)
        {
            if (!allowStructuralChanges)
            {
                throw new InvalidOperationException(
                    $"A changed material row for {snapshot.Source} no longer has its source buffer.");
            }
            if (world.HasBuffer<RenderMaterialBinding>(renderEntity))
                world.RemoveBuffer<RenderMaterialBinding>(renderEntity);
            return;
        }

        if (!world.HasBuffer<RenderMaterialBinding>(renderEntity))
        {
            if (!allowStructuralChanges)
            {
                throw new InvalidOperationException(
                    $"Render mirror {renderEntity} has no material buffer for changed source " +
                    $"{snapshot.Source}.");
            }
            world.AddBuffer<RenderMaterialBinding>(renderEntity);
        }
        else
        {
            MaterialSliceComparison comparison = new(
                _materials,
                snapshot.MaterialOffset,
                snapshot.MaterialCount);
            world.ExecuteBufferRead<RenderMaterialBinding, MaterialSliceComparison>(
                renderEntity,
                ref comparison,
                CompareMaterials);
            if (comparison.Equal)
                return;
        }

        MaterialSlice write = new(_materials, snapshot.MaterialOffset, snapshot.MaterialCount);
        world.ExecuteBufferWrite<RenderMaterialBinding, MaterialSlice>(
            renderEntity,
            ref write,
            static (DynamicBuffer<RenderMaterialBinding> buffer, ref MaterialSlice source) =>
            {
                buffer.Clear();
                buffer.EnsureCapacity(source.Count);
                for (int index = 0; index < source.Count; index++)
                {
                    buffer.Add(
                        new RenderMaterialBinding(source.Values[source.Offset + index].MaterialId));
                }
            });
    }

    private static void CompareMaterials(
        BufferView<RenderMaterialBinding> buffer, ref MaterialSliceComparison state)
    {
        ReadOnlySpan<RenderMaterialBinding> current = buffer.AsSpan();
        if (current.Length != state.Count)
        {
            state.Equal = false;
            return;
        }
        for (int index = 0; index < current.Length; index++)
            if (current[index].MaterialId != state.Values[state.Offset + index].MaterialId)
            {
                state.Equal = false;
                return;
            }
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
        SomeEngine.Assets.Pipeline.MeshPayloadSource? PageSource);

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
