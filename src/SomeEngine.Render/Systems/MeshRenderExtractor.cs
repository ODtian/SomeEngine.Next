using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Systems;

/// <summary>Extracts mesh, transform, and material-binding semantics.</summary>
internal sealed class MeshRenderExtractor : IRenderExtractionSystem
{
    private readonly List<MeshSnapshot> _snapshots = [];
    private readonly List<AssetHandle<Material>> _materials = [];
    private readonly HashSet<Entity> _sources = [];

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<WorldTransform>();
        query.ReadOptional<MeshInstance>();
        query.ReadOptionalBuffer<MeshMaterialBinding>();
    }

    public void Reset()
    {
        _snapshots.Clear();
        _materials.Clear();
        _sources.Clear();
    }

    public void Collect(QueryChunkView chunk)
    {
        if (!chunk.TryRead<WorldTransform>(out ReadOnlySpan<WorldTransform> transforms) ||
            !chunk.TryRead<MeshInstance>(out ReadOnlySpan<MeshInstance> meshes))
        {
            return;
        }

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
                for (int index = 0; index < bindings.Length; index++)
                    _materials.Add(bindings[index].Material);
            }

            _snapshots.Add(new MeshSnapshot(
                source,
                new RenderTransform(transforms[row].Qvvs),
                meshes[row].Mesh,
                meshes[row].BoundsExpansion,
                hasMaterialBuffer,
                materialOffset,
                materialCount,
                MeshChanged: true,
                MaterialsChanged: true));
            _sources.Add(source);
        }
    }

    internal bool CollectChanges(QueryChunkView chunk)
    {
        if (!chunk.Has<WorldTransform>() ||
            !chunk.TryRead<MeshInstance>(out ReadOnlySpan<MeshInstance> meshes))
        {
            return false;
        }

        bool meshChunkChanged =
            chunk.HasChangedSinceLastSystemVersion<MeshInstance>();
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
            if (!meshChanged && !materialsChanged)
                continue;

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

            MeshInstance mesh = meshes[row];
            _snapshots.Add(new MeshSnapshot(
                entities[row],
                default,
                mesh.Mesh,
                mesh.BoundsExpansion,
                hasMaterialBuffer,
                materialOffset,
                materialCount,
                meshChanged,
                materialsChanged));
        }

        return _snapshots.Count != firstSnapshot;
    }

    public void Apply(RenderExtractionContext context)
    {
        for (int index = 0; index < _snapshots.Count; index++)
        {
            MeshSnapshot snapshot = _snapshots[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            context.UpsertTransform(entity, snapshot.Transform);
            context.Upsert(entity, new RenderInstance());
            context.Upsert(entity, new RenderMesh(snapshot.Mesh, snapshot.BoundsExpansion));
            SyncMaterials(context.World, snapshot, entity, allowStructuralChanges: true);
        }

        IReadOnlyList<RenderMirror> mirrors = context.Mirrors;
        for (int index = 0; index < mirrors.Count; index++)
        {
            RenderMirror mirror = mirrors[index];
            if (_sources.Contains(mirror.Source))
                continue;

            context.RemoveIfExists<RenderTransform>(mirror.RenderEntity);
            context.RemoveIfExists<RenderPreviousTransform>(mirror.RenderEntity);
            context.RemoveIfExists<RenderInstance>(mirror.RenderEntity);
            context.RemoveIfExists<RenderMesh>(mirror.RenderEntity);
            if (context.World.HasBuffer<RenderMaterialBinding>(mirror.RenderEntity))
                context.World.RemoveBuffer<RenderMaterialBinding>(mirror.RenderEntity);
        }
    }

    internal void ApplyChanges(RenderExtractionContext context)
    {
        for (int index = 0; index < _snapshots.Count; index++)
        {
            MeshSnapshot snapshot = _snapshots[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.MeshChanged)
            {
                context.UpdateExisting(
                    entity,
                    new RenderMesh(snapshot.Mesh, snapshot.BoundsExpansion));
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

    internal void ValidateChanges(RenderExtractionContext context)
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
                static (BufferView<RenderMaterialBinding> buffer, ref MaterialSliceComparison state) =>
                {
                    ReadOnlySpan<RenderMaterialBinding> current = buffer.AsSpan();
                    if (current.Length != state.Count)
                    {
                        state.Equal = false;
                        return;
                    }
                    for (int index = 0; index < current.Length; index++)
                    {
                        if (current[index].Material == state.Values[state.Offset + index])
                            continue;
                        state.Equal = false;
                        return;
                    }
                });
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

    private readonly record struct MeshSnapshot(
        Entity Source,
        RenderTransform Transform,
        AssetHandle<Mesh> Mesh,
        float BoundsExpansion,
        bool HasMaterialBuffer,
        int MaterialOffset,
        int MaterialCount,
        bool MeshChanged,
        bool MaterialsChanged);

    private readonly record struct MaterialSlice(
        List<AssetHandle<Material>> Values,
        int Offset,
        int Count);

    private struct MaterialSliceComparison(
        List<AssetHandle<Material>> values,
        int offset,
        int count)
    {
        internal List<AssetHandle<Material>> Values = values;
        internal int Offset = offset;
        internal int Count = count;
        internal bool Equal = true;
    }
}
