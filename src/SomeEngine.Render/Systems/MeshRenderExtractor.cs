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
    private readonly Dictionary<Entity, MeshSourceGroup> _sourceMeshes = [];
    private readonly Dictionary<Mesh, MeshSources> _meshes = [];
    private readonly Dictionary<MeshBindings, MeshSourceGroup> _meshGroups = [];
    private readonly Dictionary<Entity, MeshInstance> _collectedMeshSources = [];
    private RenderWorld? _renderWorld;
    private QueryHandle _meshQuery;
    private int _remainingMeshGroups;
    private uint _collectionIndex;

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
        _collectedMeshSources[source] = instance;
        return instance.Mesh.CaptureData();
    }

    private void UntrackMesh(Entity source)
    {
        if (!_sourceMeshes.Remove(source, out MeshSourceGroup? group)) return;
        group.Sources.Remove(source);
        if (group.Sources.Count != 0) return;
        _meshGroups.Remove(group.Bindings);
        if (--group.Mesh.GroupCount == 0) _meshes.Remove(group.Mesh.Mesh);
    }

    public bool CompleteCollection(RenderExtractionContext context, bool fullCollection)
    {
        if (fullCollection)
        {
            foreach (Entity source in context.ChangedSources)
            {
                if (!_collectedMeshSources.ContainsKey(source)) UntrackMesh(source);
            }
        }

        foreach ((Entity source, MeshInstance instance) in _collectedMeshSources)
        {
            UntrackMesh(source);
            var key = new MeshBindings(instance.Mesh, instance.BoundsExpansion, context.ReadSourceMaterials(source));
            if (!_meshGroups.TryGetValue(key, out MeshSourceGroup? group))
            {
                if (!_meshes.TryGetValue(instance.Mesh, out MeshSources? mesh))
                    _meshes.Add(instance.Mesh, mesh = new MeshSources(instance.Mesh));
                _meshGroups.Add(key, group = new MeshSourceGroup(mesh, key));
                mesh.GroupCount++;
            }
            group.Sources.Add(source, instance);
            _sourceMeshes.Add(source, group);
        }

        for (int index = 0; index < _snapshots.Count; index++)
        {
            MeshSnapshot value = _snapshots[index];
            _snapshots[index] = value with
            {
                BoundsExpansion = value.BoundsExpansion + (_sourceMeshes[value.Source].Bindings.Materials?.BoundsExpansion ?? 0),
            };
        }

        int before = _snapshots.Count;
        _remainingMeshGroups = _meshGroups.Count;
        _collectionIndex++;
        if (_remainingMeshGroups != 0)
        {
            if (!_meshQuery.IsValid)
            {
                _renderWorld = context.World;
                _meshQuery = _renderWorld.Query(new QueryDefinitionBuilder().Read<RenderSource>().Read<RenderMesh>());
            }
            var extractor = this;
            _renderWorld!.ExecuteQuery(_meshQuery, ref extractor,
                static (scoped QueryCursor query, ref MeshRenderExtractor extractor) => extractor.CollectMeshChanges(query));
        }
        return _snapshots.Count != before;
    }

    private void CollectMeshChanges(scoped QueryCursor query)
    {
        foreach (QueryChunkView chunk in query.Chunks)
        {
            ReadOnlySpan<RenderSource> sources = chunk.Read<RenderSource>();
            ReadOnlySpan<RenderMesh> meshes = chunk.Read<RenderMesh>();
            for (int row = 0; row < sources.Length; row++)
            {
                Entity source = sources[row].Entity;
                if (_collectedMeshSources.ContainsKey(source) ||
                    !_sourceMeshes.TryGetValue(source, out MeshSourceGroup? group) || group.Collection == _collectionIndex) continue;
                group.Collection = _collectionIndex;
                MeshSources mesh = group.Mesh;
                if (mesh.Collection != _collectionIndex)
                {
                    mesh.Collection = _collectionIndex;
                    mesh.Revision = mesh.Mesh.Revision;
                    mesh.Data = null;
                }
                float expansion = group.Bindings.BoundsExpansion + (group.Bindings.Materials?.BoundsExpansion ?? 0);
                RenderMesh current = meshes[row];
                // Uncollected members share the preceding successful apply. Changed source rows
                // are replayed until that same transaction accepts their ECS versions.
                if (current.Mesh.Revision != mesh.Revision || current.BoundsExpansion != expansion)
                    CollectMeshChanges(group, expansion);
                if (--_remainingMeshGroups == 0) return;
            }
        }
    }

    private void CollectMeshChanges(MeshSourceGroup group, float expansion)
    {
        Mesh data = group.Mesh.Data ??= group.Mesh.Mesh.CaptureData();
        foreach ((Entity source, MeshInstance instance) in group.Sources)
        {
            if (_collectedMeshSources.ContainsKey(source)) continue;
            _snapshots.Add(new MeshSnapshot(source, default, data, expansion,
                false, 0, 0, MeshChanged: true, MaterialsChanged: false, instance.PageSource,
                instance.DepthCacheClass, instance.FirstPerson));
            _meshSources.Add(source);
        }
    }

    public void Dispose()
    {
        Reset();
        _sourceMeshes.Clear();
        _meshes.Clear();
        _meshGroups.Clear();
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
                TrackMesh(source, meshes[row]),
                meshes[row].BoundsExpansion,
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
                    mesh.BoundsExpansion,
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

    private readonly record struct MeshBindings(Mesh Mesh, float BoundsExpansion, RenderSourceMaterials? Materials);

    private sealed class MeshSources(Mesh mesh)
    {
        internal Mesh Mesh { get; } = mesh;
        internal int GroupCount;
        internal uint Collection;
        internal ulong Revision;
        internal Mesh? Data;
    }

    private sealed class MeshSourceGroup(MeshSources mesh, MeshBindings bindings)
    {
        internal MeshSources Mesh { get; } = mesh;
        internal MeshBindings Bindings { get; } = bindings;
        internal Dictionary<Entity, MeshInstance> Sources { get; } = [];
        internal uint Collection;
    }

}
