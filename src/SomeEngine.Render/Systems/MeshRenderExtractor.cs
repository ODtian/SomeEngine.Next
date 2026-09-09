using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Systems;

/// <summary>Extracts mesh, transform, and material-binding semantics.</summary>
public sealed class MeshRenderExtractor : IRenderExtractionSystem
{
    private readonly List<MeshSnapshot> _snapshots = [];
    private readonly List<InstancedSnapshot> _instancedSnapshots = [];
    private readonly List<MaterialData> _materials = [];
    private readonly HashSet<Entity> _meshSources = [];
    private readonly HashSet<Entity> _instancedSources = [];
    private readonly Dictionary<Entity, Material[]> _sourceMaterials = [];
    private readonly Dictionary<Material, HashSet<Entity>> _materialSources = [];
    private readonly HashSet<Entity> _changedMaterialSources = [];
    private readonly HashSet<Entity> _collectedMaterialSources = [];
    private readonly object _assetChangesGate = new();
    private readonly Dictionary<Material, MaterialData> _capturedMaterials = [];
    private readonly Dictionary<Entity, MeshInstance> _sourceMeshes = [];
    private readonly Dictionary<Mesh, HashSet<Entity>> _meshAssetSources = [];
    private readonly HashSet<Entity> _changedMeshSources = [];
    private readonly HashSet<Entity> _collectedMeshSources = [];
    private readonly Dictionary<Material, Texture[]> _materialTextures = [];
    private readonly Dictionary<Texture, HashSet<Material>> _textureMaterials = [];

    public bool CanApplyChangesDirect => false;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.Write<RenderMesh>();
        query.Write<RenderInstancedMesh>();
        query.ReadOptional<WorldTransform>();
        query.ReadOptional<MeshInstance>();
        query.ReadOptional<InstancedMesh>();
        query.ReadOptionalBuffer<MeshMaterialBinding>();
    }

    public void Reset()
    {
        _snapshots.Clear();
        _instancedSnapshots.Clear();
        _materials.Clear();
        _meshSources.Clear();
        _instancedSources.Clear();
        _collectedMaterialSources.Clear();
        _capturedMaterials.Clear();
        _collectedMeshSources.Clear();
    }

    private MaterialData CaptureMaterial(Material material)
    {
        if (_capturedMaterials.TryGetValue(material, out MaterialData? data)) return data;
        data = material.CaptureData();
        _capturedMaterials.Add(material, data);
        return data;
    }

    private MeshData TrackMesh(Entity source, in MeshInstance instance)
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
                    material.Changed += CollectMaterialChange;
                    TrackMaterialTextures(material);
                }
                sources.Add(source);
            }
            _sourceMaterials.Add(source, materials);
            _collectedMaterialSources.Add(source);
            _changedMaterialSources.Add(source);
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
            material.Changed -= CollectMaterialChange;
            UntrackMaterialTextures(material);
            _materialSources.Remove(material);
        }
        _changedMaterialSources.Remove(source);
    }

    private void CollectMaterialChange(Material material)
    {
        lock (_assetChangesGate)
            if (_materialSources.TryGetValue(material, out var sources))
            {
                TrackMaterialTextures(material);
                _changedMaterialSources.UnionWith(sources);
            }
    }

    private void TrackMaterialTextures(Material material)
    {
        UntrackMaterialTextures(material);
        Texture[] textures = material.CaptureTextureReferences();
        _materialTextures.Add(material, textures);
        foreach (Texture texture in textures)
        {
            if (!_textureMaterials.TryGetValue(texture, out var materials))
            {
                _textureMaterials.Add(texture, materials = []);
                texture.Changed += CollectTextureChange;
            }
            materials.Add(material);
        }
    }

    private void UntrackMaterialTextures(Material material)
    {
        if (!_materialTextures.Remove(material, out Texture[]? textures)) return;
        foreach (Texture texture in textures)
        {
            HashSet<Material> materials = _textureMaterials[texture];
            materials.Remove(material);
            if (materials.Count != 0) continue;
            texture.Changed -= CollectTextureChange;
            _textureMaterials.Remove(texture);
        }
    }

    private void CollectTextureChange(Texture texture)
    {
        lock (_assetChangesGate)
            if (_textureMaterials.TryGetValue(texture, out var materials))
                foreach (Material material in materials)
                    _changedMaterialSources.UnionWith(_materialSources[material]);
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
                MeshData data = instance.Mesh.CaptureData();
                Entity entity = context.RequireMirror(source);
                if (ReferenceEquals(context.World.Read<RenderMesh>(entity).Mesh, data))
                {
                    _changedMeshSources.Remove(source);
                    continue;
                }
                _snapshots.Add(new MeshSnapshot(source, default, data, instance.BoundsExpansion,
                    false, 0, 0, MeshChanged: true, MaterialsChanged: false));
                _meshSources.Add(source);
                changed = true;
            }
            foreach (Entity source in _changedMaterialSources.ToArray())
            {
                if (_collectedMaterialSources.Contains(source)) continue;
                Material[] materials = _sourceMaterials[source];
                int offset = _materials.Count;
                foreach (Material material in materials) _materials.Add(CaptureMaterial(material));
                Entity entity = context.RequireMirror(source);
                var comparison = new MaterialSliceComparison(_materials, offset, materials.Length);
                context.World.ExecuteBufferRead<RenderMaterialBinding, MaterialSliceComparison>(
                    entity, ref comparison, CompareMaterials);
                if (comparison.Equal)
                {
                    // Only consume a notification after the Render World contains its data.
                    // A failed extraction therefore leaves the same source eligible for retry.
                    bool current = true;
                    foreach (Material material in materials)
                        if (!ReferenceEquals(material.CaptureData(), _capturedMaterials[material]))
                            current = false;
                    if (current) _changedMaterialSources.Remove(source);
                    _materials.RemoveRange(offset, materials.Length);
                    continue;
                }
                _snapshots.Add(new MeshSnapshot(source, default, null!, 0, true,
                    offset, materials.Length, MeshChanged: false, MaterialsChanged: true));
                _meshSources.Add(source);
                changed = true;
            }
            return changed;
        }
    }

    public void Dispose()
    {
        lock (_assetChangesGate)
        {
            foreach (Material material in _materialSources.Keys)
                material.Changed -= CollectMaterialChange;
            _materialSources.Clear();
            _sourceMaterials.Clear();
            _changedMaterialSources.Clear();
            foreach (Mesh mesh in _meshAssetSources.Keys) mesh.Changed -= CollectMeshChange;
            _meshAssetSources.Clear();
            _sourceMeshes.Clear();
            _changedMeshSources.Clear();
            foreach (Texture texture in _textureMaterials.Keys) texture.Changed -= CollectTextureChange;
            _textureMaterials.Clear();
            _materialTextures.Clear();
        }
    }

    public void Collect(QueryChunkView chunk)
    {
        if (!chunk.TryRead<WorldTransform>(out ReadOnlySpan<WorldTransform> transforms))
            return;

        bool hasMeshes = chunk.TryRead<MeshInstance>(out ReadOnlySpan<MeshInstance> meshes);
        bool hasInstancedMeshes =
            chunk.TryRead<InstancedMesh>(out ReadOnlySpan<InstancedMesh> instancedMeshes);
        if (!hasMeshes && !hasInstancedMeshes)
            return;

        ReadOnlySpan<Entity> entities = chunk.Entities;
        bool hasMaterialBuffer = chunk.HasBuffer<MeshMaterialBinding>();
        for (int row = 0; row < entities.Length; row++)
        {
            Entity source = entities[row];
            if (hasMeshes)
            {
                int materialOffset = _materials.Count;
                int materialCount = 0;
                if (hasMaterialBuffer)
                {
                    ReadOnlySpan<MeshMaterialBinding> bindings =
                        chunk.ReadBuffer<MeshMaterialBinding>(row).AsSpan();
                    materialCount = bindings.Length;
                    TrackMaterials(entities[row], bindings);
                    for (int index = 0; index < bindings.Length; index++)
                        _materials.Add(CaptureMaterial(bindings[index].Material));
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
                    MaterialsChanged: true));
                _meshSources.Add(source);
            }

            if (hasInstancedMeshes)
            {
                _instancedSnapshots.Add(new InstancedSnapshot(
                    source,
                    new RenderTransform(transforms[row].Qvvs),
                    instancedMeshes[row].Resource,
                    Changed: true));
                _instancedSources.Add(source);
            }
        }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (!chunk.Has<WorldTransform>())
            return false;

        bool hasMeshes = chunk.TryRead<MeshInstance>(out ReadOnlySpan<MeshInstance> meshes);
        bool hasInstancedMeshes =
            chunk.TryRead<InstancedMesh>(out ReadOnlySpan<InstancedMesh> instancedMeshes);
        if (!hasMeshes && !hasInstancedMeshes)
            return false;

        if (chunk.HasChangedSinceLastSystemVersion<WorldTransform>())
            context.CollectTransformChanges(chunk);

        bool meshChunkChanged = hasMeshes &&
            chunk.HasChangedSinceLastSystemVersion<MeshInstance>();
        bool hasMaterialBuffer = chunk.HasBuffer<MeshMaterialBinding>();
        bool materialChunkChanged =
            hasMaterialBuffer &&
            chunk.HasBufferChangedSinceLastSystemVersion<MeshMaterialBinding>();
        bool instancedChunkChanged = hasInstancedMeshes &&
            chunk.HasChangedSinceLastSystemVersion<InstancedMesh>();
        if (!meshChunkChanged && !materialChunkChanged && !instancedChunkChanged)
            return false;

        int firstSnapshot = _snapshots.Count;
        int firstInstancedSnapshot = _instancedSnapshots.Count;
        ReadOnlySpan<Entity> entities = chunk.Entities;
        for (int row = 0; row < entities.Length; row++)
        {
            bool meshChanged =
                meshChunkChanged &&
                chunk.RowChangedSinceLastSystemVersion<MeshInstance>(row);
            bool materialsChanged =
                materialChunkChanged &&
                chunk.RowBufferChangedSinceLastSystemVersion<MeshMaterialBinding>(row);
            if (hasMeshes && (meshChanged || materialsChanged))
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
                        _materials.Add(CaptureMaterial(bindings[index].Material));
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
                    materialsChanged));
            }

            bool instancedChanged = instancedChunkChanged &&
                chunk.RowChangedSinceLastSystemVersion<InstancedMesh>(row);
            if (instancedChanged)
            {
                _instancedSnapshots.Add(new InstancedSnapshot(
                    entities[row],
                    default,
                    instancedMeshes[row].Resource,
                    Changed: true));
            }
        }

        return _snapshots.Count != firstSnapshot ||
            _instancedSnapshots.Count != firstInstancedSnapshot;
    }

    public void Apply(RenderExtractionContext context)
    {
        for (int index = 0; index < _snapshots.Count; index++)
        {
            MeshSnapshot snapshot = _snapshots[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            if (snapshot.Transform is { } transform) context.RetainTransform(entity, transform);
            context.Upsert(entity, new RenderInstance());
            if (snapshot.MeshChanged)
                context.Upsert(entity, new RenderMesh(snapshot.Mesh, snapshot.BoundsExpansion));
            if (snapshot.MaterialsChanged)
                SyncMaterials(context.World, snapshot, entity, allowStructuralChanges: true);
        }

        for (int index = 0; index < _instancedSnapshots.Count; index++)
        {
            InstancedSnapshot snapshot = _instancedSnapshots[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            context.RetainTransform(entity, snapshot.Transform);
            context.Upsert(entity, new RenderInstancedMesh(snapshot.Resource));
        }

        ReadOnlySpan<RenderMirror> mirrors = context.Mirrors;
        for (int index = 0; index < mirrors.Length; index++)
        {
            RenderMirror mirror = mirrors[index];
            bool retainedMesh = _meshSources.Contains(mirror.Source);
            bool retainedInstanced = _instancedSources.Contains(mirror.Source);
            if (!retainedMesh)
            {
                context.RemoveIfExists<RenderInstance>(mirror.RenderEntity);
                context.RemoveIfExists<RenderMesh>(mirror.RenderEntity);
                if (context.World.HasBuffer<RenderMaterialBinding>(mirror.RenderEntity))
                    context.World.RemoveBuffer<RenderMaterialBinding>(mirror.RenderEntity);
            }
            if (!retainedInstanced)
                context.RemoveIfExists<RenderInstancedMesh>(mirror.RenderEntity);
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
                        snapshot.BoundsExpansion));
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


        for (int index = 0; index < _instancedSnapshots.Count; index++)
        {
            InstancedSnapshot snapshot = _instancedSnapshots[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            context.UpdateExisting(entity, new RenderInstancedMesh(snapshot.Resource));
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


        for (int index = 0; index < _instancedSnapshots.Count; index++)
        {
            InstancedSnapshot snapshot = _instancedSnapshots[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            context.RequireExisting<RenderInstancedMesh>(entity);
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
                        new RenderMaterialBinding(source.Values[source.Offset + index]));
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
            if (!ReferenceEquals(current[index].Material, state.Values[state.Offset + index]))
            {
                state.Equal = false;
                return;
            }
    }

    private readonly record struct MeshSnapshot(
        Entity Source,
        RenderTransform? Transform,
        MeshData Mesh,
        float BoundsExpansion,
        bool HasMaterialBuffer,
        int MaterialOffset,
        int MaterialCount,
        bool MeshChanged,
        bool MaterialsChanged);

    private readonly record struct InstancedSnapshot(
        Entity Source,
        RenderTransform Transform,
        RenderMeshInstanceHandle Resource,
        bool Changed);

    private readonly record struct MaterialSlice(
        List<MaterialData> Values,
        int Offset,
        int Count);

    private struct MaterialSliceComparison(
        List<MaterialData> values,
        int offset,
        int count)
    {
        internal List<MaterialData> Values = values;
        internal int Offset = offset;
        internal int Count = count;
        internal bool Equal = true;
    }
}
