using SomeEngine.Assets;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Materials;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Render.Systems;

public sealed partial class RenderWorldExtractor
{
    private readonly RenderWorld _renderWorld;
    private readonly Dictionary<EntityId, EntityId> _instanceEntities = [];
    private readonly Dictionary<EntityId, RenderInstance> _previous = [];
    private readonly List<int> _freeIndices = [];
    private readonly List<EntityId> _linkSources = [];
    private readonly List<EntityId> _linkTargets = [];
    private readonly List<int> _linkIndices = [];
    private readonly HashSet<EntityId> _removedSources = [];
    private readonly List<DirectionalLight> _directionalLights = [];
    private readonly List<PointLight> _pointLights = [];
    private readonly List<SpotLight> _spotLights = [];
}

public sealed partial class RenderWorldExtractor
{
    private World? _sourceWorld;
    private QueryHandle _sourceQuery;
    private QueryHandle _unlinkedSources;
    private QueryHandle _changedTransforms;
    private QueryHandle _changedOverrides;
    private QueryHandle _changedMesh;
    private QueryHandle _lostMesh;
    private QueryHandle _lostTransform;
    private QueryHandle _removedMesh;
    private QueryHandle _removedTransform;
    private QueryHandle _changedBindings;
    private QueryHandle _removedBindings;
    private QueryHandle _removedOverride;
    private QueryHandle _sceneLights;
    private QueryHandle _addedSceneLights;
    private QueryHandle _changedSceneLights;
    private QueryHandle _removedSceneLights;
    private QueryHandle _renderInstances;
}

public sealed partial class RenderWorldExtractor
{
    private uint _lastSourceVersion;
    private int _nextIndex;
    private bool _built;
    private uint _version;
    private uint _shapeVersion;
    private uint _instanceShapeVersion;
    private uint _materialVersion;
    private uint _denseSourceSlotShapeVersion;
    private bool _denseSourceSlots;

    public RenderWorldExtractor(RenderWorld renderWorld)
    {
        _renderWorld = renderWorld ?? throw new ArgumentNullException(nameof(renderWorld));
        _renderInstances = _renderWorld.World.Query(
            new QueryDefinitionBuilder()
                .Read<RenderInstance>());
    }

    public uint Version => _version;
    public uint ShapeVersion => _shapeVersion;

    public void Rebuild(World sourceWorld)
    {
        ArgumentNullException.ThrowIfNull(sourceWorld);
        uint current = sourceWorld.AcquireSystemTick();
        EnsureQueries(sourceWorld);
        _renderWorld.ClearInstanceUpdates();
        if (!_built)
        {
            BuildInitialState(sourceWorld, current);
            _lastSourceVersion = current;
            return;
        }

        ApplyIncrementalState(sourceWorld, current);
        _lastSourceVersion = current;
    }

    private void BuildInitialState(World sourceWorld, uint current)
    {
        using (Profiler.BeginScope("RenderWorldExtractor.Build"))
        {
            CapturePrevious();
            _renderWorld.Reset();
            Build(sourceWorld, current);
            CollectLights(sourceWorld);
            _renderWorld.LightVersion = 1;
            _materialVersion = 1;
            _renderWorld.MaterialVersion = _materialVersion;
            IndexInstances();
            _built = true;
            _version++;
            _renderWorld.Version = _version;
            BumpInstanceShapeVersion();
            TouchShape();
        }
    }

    private void ApplyIncrementalState(World sourceWorld, uint current)
    {
        bool changed;
        bool materialChanged;
        bool lightsChanged;
        bool instanceShapeChanged;
        bool lightsShapeChanged;
        using (Profiler.BeginScope("RenderWorldExtractor.Apply"))
        {
            using (Profiler.BeginScope("RenderWorldExtractor.ApplyInstances"))
            {
                changed = Apply(sourceWorld, current, out instanceShapeChanged, out materialChanged);
            }

            using (Profiler.BeginScope("RenderWorldExtractor.ApplyLights"))
            {
                lightsChanged = ApplyLights(sourceWorld, current, out lightsShapeChanged);
            }
        }

        if (changed || lightsChanged)
        {
            _version++;
            _renderWorld.Version = _version;
        }
        if (lightsChanged)
        {
            _renderWorld.LightVersion++;
            if (_renderWorld.LightVersion == 0)
                _renderWorld.LightVersion = 1;
        }
        if (materialChanged)
        {
            _materialVersion++;
            if (_materialVersion == 0)
                _materialVersion = 1;
            _renderWorld.MaterialVersion = _materialVersion;
        }
        if (instanceShapeChanged)
        {
            BumpInstanceShapeVersion();
            TouchShape();
        }
        else if (lightsShapeChanged)
        {
            TouchShape();
        }
    }

    private void BumpInstanceShapeVersion()
    {
        _instanceShapeVersion++;
        if (_instanceShapeVersion == 0)
            _instanceShapeVersion = 1;
        _renderWorld.InstanceShapeVersion = _instanceShapeVersion;
    }

    private void Build(World sourceWorld, uint current)
    {
        EnsureQueries(sourceWorld);
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        int instanceIndex = 0;
        foreach (var chunk in sourceWorld.RunQuery(_sourceQuery, 0, current).Chunks)
            AddInitialChunk(chunk, ref instanceIndex);

        LinkSources(sourceWorld);
        _nextIndex = instanceIndex;
        _renderWorld.InstanceCount = instanceIndex;
    }

    private void AddInitialChunk(QueryChunkView chunk, ref int instanceIndex)
    {
        var entities = chunk.Entities;
        var transforms = chunk.Read<WorldTransform>();
        var meshes = chunk.Read<MeshInstance>();
        bool hasBindings = chunk.TryRead<MeshMaterialBindings>(out var bindings);
        bool hasOverrides = chunk.TryRead<MaterialOverride>(out var overrides);

        for (int i = 0; i < entities.Length; i++)
            AddInitialInstance(
                entities[i],
                transforms[i],
                meshes[i],
                hasBindings ? bindings[i].Materials.ToArray() : [],
                hasOverrides,
                hasOverrides ? overrides[i] : default,
                ref instanceIndex);
    }

    private void AddInitialInstance(
        EntityId source,
        WorldTransform transform,
        MeshInstance mesh,
        Handle<Material>[] materials,
        bool hasOverride,
        MaterialOverride materialOverride,
        ref int instanceIndex)
    {
        var instance = CreateInitialInstance(source, transform, mesh, hasOverride, ref instanceIndex);
        var renderEntity = AttachRenderInstance(source, materials, hasOverride, materialOverride, in instance);
        TrackLink(source, renderEntity, instance.InstanceIndex);
    }

    private RenderInstance CreateInitialInstance(
        EntityId source,
        WorldTransform transform,
        MeshInstance mesh,
        bool hasOverride,
        ref int instanceIndex)
    {
        var gpuTransform = GpuTransform.FromQvvs(transform.Qvvs);
        var old = _previous.TryGetValue(source, out var previous) ? previous : default;
        return new RenderInstance
        {
            SourceEntity = source,
            InstanceIndex = instanceIndex++,
            Transform = gpuTransform,
            PrevTransform = old.SourceEntity == source ? old.Transform : gpuTransform,
            Mesh = mesh.Mesh,
            DataOffset = old.SourceEntity == source ? old.DataOffset : 0,
            DataFlags = hasOverride ? InstanceFlags.MaterialOverride : InstanceFlags.None,
            BoundsExpansion = MathF.Max(0f, mesh.BoundsExpansion),
        };
    }

    private EntityId AttachRenderInstance(
        EntityId source,
        Handle<Material>[] materials,
        bool hasOverride,
        MaterialOverride materialOverride,
        in RenderInstance instance)
    {
        var renderEntity = _renderWorld.World.CreateEntity();
        _renderWorld.World.Add(renderEntity, new RenderSourceEntity { SourceEntity = source });
        _renderWorld.World.Add(renderEntity, new RenderMaterials { Materials = materials });
        InstanceMarks.Write(_renderWorld.World, renderEntity, instance, InstanceDirtyFlags.All);
        if (hasOverride)
            _renderWorld.World.Add(renderEntity, materialOverride);
        _renderWorld.StoreInstance(renderEntity, in instance, hasOverride, in materialOverride);
        return renderEntity;
    }

    private bool Apply(World sourceWorld, uint current, out bool shapeChanged, out bool materialChanged)
    {
        bool changed = false;
        shapeChanged = false;
        materialChanged = false;
        _removedSources.Clear();
        bool removedLost = RunExtractorScope("RenderWorldExtractor.RemoveLost", () => RemoveLost(sourceWorld, current));
        bool removedSources = RunExtractorScope("RenderWorldExtractor.RemoveSources", () => RemoveSources(sourceWorld, current));
        bool addedSources = RunExtractorScope("RenderWorldExtractor.AddSources", () => AddSources(sourceWorld, current));
        changed |= removedLost;
        changed |= removedSources;
        changed |= addedSources;
        shapeChanged |= removedLost;
        shapeChanged |= removedSources;
        shapeChanged |= addedSources;
        materialChanged |= removedLost;
        materialChanged |= removedSources;
        materialChanged |= addedSources;
        bool instanceDataChanged = UpdateChangedInstancesScoped(sourceWorld, current, out bool instanceDataShapeChanged);
        changed |= instanceDataChanged;
        shapeChanged |= instanceDataShapeChanged;

        bool meshesChanged = RunExtractorScope("RenderWorldExtractor.UpdateMeshes", () => UpdateMeshes(sourceWorld, current));
        bool bindingsChanged = RunExtractorScope("RenderWorldExtractor.UpdateBindings", () => UpdateBindings(sourceWorld, current));
        bool bindingsRemoved = RunExtractorScope("RenderWorldExtractor.RemoveBindings", () => RemoveBindings(sourceWorld, current));
        changed |= meshesChanged;
        changed |= bindingsChanged;
        changed |= bindingsRemoved;
        shapeChanged |= meshesChanged;
        shapeChanged |= bindingsChanged;
        shapeChanged |= bindingsRemoved;
        materialChanged |= meshesChanged;
        materialChanged |= bindingsChanged;
        materialChanged |= bindingsRemoved;
        bool overridesRemoved = RemoveOverridesScoped(sourceWorld, current, out bool overridesRemovedShapeChanged);
        changed |= overridesRemoved;
        shapeChanged |= overridesRemovedShapeChanged;
        return changed;
    }

    private static bool RunExtractorScope(string name, Func<bool> action)
    {
        using (Profiler.BeginScope(name))
            return action();
    }

    private bool UpdateChangedInstancesScoped(World sourceWorld, uint current, out bool shapeChanged)
    {
        using (Profiler.BeginScope("RenderWorldExtractor.UpdateChangedInstances"))
            return UpdateChangedInstances(sourceWorld, current, out shapeChanged);
    }

    private bool RemoveOverridesScoped(World sourceWorld, uint current, out bool shapeChanged)
    {
        using (Profiler.BeginScope("RenderWorldExtractor.RemoveOverrides"))
            return RemoveOverrides(sourceWorld, current, out shapeChanged);
    }

    private bool RemoveSources(World sourceWorld, uint current)
    {
        bool changed = false;
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_removedMesh, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (!_removedSources.Add(source))
                    continue;

                _linkSources.Add(source);
                changed |= chunk.TryRead(row, out RenderSourceLink link)
                    ? RemoveSource(source, link.RenderEntity)
                    : RemoveSource(source);
            }
        }

        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_removedTransform, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (!_removedSources.Add(source))
                    continue;

                _linkSources.Add(source);
                changed |= chunk.TryRead(row, out RenderSourceLink link)
                    ? RemoveSource(source, link.RenderEntity)
                    : RemoveSource(source);
            }
        }

        ClearLinks(sourceWorld);
        sourceWorld.ClearRemoved<MeshInstance>(current);
        sourceWorld.ClearRemoved<WorldTransform>(current);
        return changed;
    }

    private bool RemoveLost(World sourceWorld, uint current)
    {
        bool changed = false;
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_lostMesh, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (!_removedSources.Add(source))
                    continue;

                _linkSources.Add(source);
                changed |= RemoveSource(source, chunk.Read<RenderSourceLink>(row).RenderEntity);
            }
        }

        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_lostTransform, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (!_removedSources.Add(source))
                    continue;

                _linkSources.Add(source);
                changed |= RemoveSource(source, chunk.Read<RenderSourceLink>(row).RenderEntity);
            }
        }

        ClearLinks(sourceWorld);
        return changed;
    }

    private bool RemoveSource(EntityId source)
    {
        if (!_instanceEntities.TryGetValue(source, out EntityId renderEntity))
            return false;

        return RemoveSource(source, renderEntity);
    }

    private bool RemoveSource(EntityId source, EntityId renderEntity)
    {
        _instanceEntities.Remove(source);
        if (renderEntity == EntityId.Null || !_renderWorld.World.IsAlive(renderEntity))
            return false;

        if (_renderWorld.World.TryRead(renderEntity, out RenderInstance instance))
        {
            _freeIndices.Add(instance.InstanceIndex);
            _renderWorld.ClearInstanceSlot(instance.InstanceIndex);
            _renderWorld.World.Remove<RenderInstance>(renderEntity);
            _denseSourceSlotShapeVersion = 0;
            _denseSourceSlots = false;
        }

        if (_renderWorld.World.IsAlive(renderEntity))
            _renderWorld.World.DestroyEntity(renderEntity);

        if (_renderWorld.InstanceCount > 0)
            _renderWorld.InstanceCount--;

        return true;
    }

    private bool AddSources(World sourceWorld, uint current)
    {
        bool changed = false;
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        changed |= AddSources(sourceWorld, _unlinkedSources, current);
        LinkSources(sourceWorld);
        return changed;
    }

    private bool AddSources(World sourceWorld, QueryHandle query, uint current)
    {
        bool changed = false;
        foreach (var chunk in sourceWorld.RunQuery(query, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                var source = chunk.GetEntity(row);
                if (_instanceEntities.ContainsKey(source))
                    continue;

                var transform = chunk.Read<WorldTransform>(row);
                var mesh = chunk.Read<MeshInstance>(row);
                bool hasBindings = chunk.TryRead(row, out MeshMaterialBindings bindings);
                bool hasOverrides = chunk.TryRead(row, out MaterialOverride materialOverride);
                AddSourceInstance(
                    source,
                    transform,
                    mesh,
                    hasBindings ? bindings.Materials.ToArray() : [],
                    hasOverrides,
                    hasOverrides ? materialOverride : default);
                changed = true;
            }
        }

        return changed;
    }

    private void AddSourceInstance(
        EntityId source,
        WorldTransform transform,
        MeshInstance mesh,
        Handle<Material>[] materials,
        bool hasOverride,
        MaterialOverride materialOverride)
    {
        var instance = CreateSourceInstance(source, transform, mesh, hasOverride);
        var renderEntity = AttachRenderInstance(source, materials, hasOverride, materialOverride, in instance);
        _instanceEntities[source] = renderEntity;
        TrackLink(source, renderEntity, instance.InstanceIndex);
        _renderWorld.InstanceCount++;
        _denseSourceSlotShapeVersion = 0;
        _denseSourceSlots = false;
    }

    private RenderInstance CreateSourceInstance(
        EntityId source,
        WorldTransform transform,
        MeshInstance mesh,
        bool hasOverride)
    {
        var gpuTransform = GpuTransform.FromQvvs(transform.Qvvs);
        return new RenderInstance
        {
            SourceEntity = source,
            InstanceIndex = AllocateIndex(),
            Transform = gpuTransform,
            PrevTransform = gpuTransform,
            Mesh = mesh.Mesh,
            DataFlags = hasOverride ? InstanceFlags.MaterialOverride : InstanceFlags.None,
            BoundsExpansion = MathF.Max(0f, mesh.BoundsExpansion),
        };
    }

    private bool UpdateChangedInstances(World sourceWorld, uint current, out bool shapeChanged)
    {
        bool changed = false;
        shapeChanged = false;
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_changedTransforms, _lastSourceVersion, current).Chunks)
        {
            bool overrideChunkChanged = chunk.Has<MaterialOverride>()
                && (int)(chunk.GetChangeVersion<MaterialOverride>() - _lastSourceVersion) > 0;
            changed |= UpdateInstanceChunk(
                chunk,
                transformChunkChanged: true,
                overrideChunkChanged,
                out bool chunkShapeChanged);
            shapeChanged |= chunkShapeChanged;
        }

        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_changedOverrides, _lastSourceVersion, current).Chunks)
        {
            if ((int)(chunk.GetChangeVersion<WorldTransform>() - _lastSourceVersion) > 0)
                continue;

            changed |= UpdateInstanceChunk(
                chunk,
                transformChunkChanged: false,
                overrideChunkChanged: true,
                out bool chunkShapeChanged);
            shapeChanged |= chunkShapeChanged;
        }

        return changed;
    }
}

public sealed partial class RenderWorldExtractor
{
    private bool UpdateInstanceChunk(
        QueryChunkView chunk,
        bool transformChunkChanged,
        bool overrideChunkChanged,
        out bool shapeChanged)
    {
        shapeChanged = false;
        var renderWorld = _renderWorld.World;
        var links = chunk.Read<RenderSourceLink>();
        var transforms = transformChunkChanged ? chunk.Read<WorldTransform>() : default;
        var overrides = overrideChunkChanged ? chunk.Read<MaterialOverride>() : default;
        var transformVersions = transformChunkChanged ? chunk.ReadWriteVersions<WorldTransform>() : default;
        var overrideVersions = overrideChunkChanged ? chunk.ReadWriteVersions<MaterialOverride>() : default;
        var slotEntities = _renderWorld.InstanceSlotEntities;
        var slotInstances = _renderWorld.InstanceSlots;
        var slotOverrides = overrideChunkChanged ? _renderWorld.InstanceSlotOverrides : default;
        var slotHasOverrides = overrideChunkChanged ? _renderWorld.InstanceSlotHasOverride : default;
        var slotActive = _renderWorld.InstanceSlotActive;
        var fullRangeDirty = BuildFullRangeDirty(transformChunkChanged, overrideChunkChanged);

        if (CanUseFullRange(
            links,
            overrideChunkChanged,
            slotEntities,
            slotInstances,
            slotOverrides,
            slotHasOverrides,
            slotActive))
        {
            return ApplyFullRangeChunk(
                renderWorld,
                links,
                transforms,
                overrides,
                transformVersions,
                overrideVersions,
                slotInstances,
                slotOverrides,
                slotHasOverrides,
                transformChunkChanged,
                overrideChunkChanged,
                fullRangeDirty,
                out shapeChanged);
        }

        return ApplySparseChunk(
            renderWorld,
            links,
            transforms,
            overrides,
            transformVersions,
            overrideVersions,
            transformChunkChanged,
            overrideChunkChanged,
            out shapeChanged);
    }

    private static InstanceDirtyFlags BuildFullRangeDirty(
        bool transformChunkChanged,
        bool overrideChunkChanged)
    {
        var dirty = InstanceDirtyFlags.None;
        if (transformChunkChanged)
            dirty |= InstanceDirtyFlags.Transform;
        if (overrideChunkChanged)
            dirty |= InstanceDirtyFlags.Data;
        return dirty;
    }

    private bool CanUseFullRange(
        ReadOnlySpan<RenderSourceLink> links,
        bool overrideChunkChanged,
        ReadOnlySpan<EntityId> slotEntities,
        ReadOnlySpan<RenderInstance> slotInstances,
        ReadOnlySpan<MaterialOverride> slotOverrides,
        ReadOnlySpan<bool> slotHasOverrides,
        ReadOnlySpan<bool> slotActive)
    {
        if (!HasFullRangeSlotCapacity(
            links,
            overrideChunkChanged,
            slotEntities,
            slotInstances,
            slotOverrides,
            slotHasOverrides,
            slotActive))
        {
            return false;
        }

        using (Profiler.BeginScope("RenderWorldExtractor.UpdateInstanceChunk.FullRangeCheck"))
        {
            var denseSourceSlots = _denseSourceSlotShapeVersion == _instanceShapeVersion
                && _denseSourceSlots;
            if (_denseSourceSlotShapeVersion != _instanceShapeVersion)
            {
                denseSourceSlots = AreSourceSlotsDense(links, slotEntities, slotInstances, slotActive);
                _denseSourceSlots = denseSourceSlots;
                _denseSourceSlotShapeVersion = _instanceShapeVersion;
            }

            return AcceptsFullRangeOverride(denseSourceSlots, overrideChunkChanged);
        }
    }

    private bool HasFullRangeSlotCapacity(
        ReadOnlySpan<RenderSourceLink> links,
        bool overrideChunkChanged,
        ReadOnlySpan<EntityId> slotEntities,
        ReadOnlySpan<RenderInstance> slotInstances,
        ReadOnlySpan<MaterialOverride> slotOverrides,
        ReadOnlySpan<bool> slotHasOverrides,
        ReadOnlySpan<bool> slotActive)
    {
        if (links.Length != _renderWorld.InstanceCount)
            return false;
        if (links.Length == 0)
            return false;
        if (slotEntities.Length < links.Length)
            return false;
        if (slotInstances.Length < links.Length)
            return false;
        if (slotActive.Length < links.Length)
            return false;

        return !overrideChunkChanged
            || HasFullRangeOverrideCapacity(links, slotOverrides, slotHasOverrides);
    }

    private static bool HasFullRangeOverrideCapacity(
        ReadOnlySpan<RenderSourceLink> links,
        ReadOnlySpan<MaterialOverride> slotOverrides,
        ReadOnlySpan<bool> slotHasOverrides)
    {
        if (slotOverrides.Length < links.Length)
            return false;
        if (slotHasOverrides.Length < links.Length)
            return false;
        return true;
    }

    private bool AcceptsFullRangeOverride(bool denseSourceSlots, bool overrideChunkChanged)
    {
        if (!denseSourceSlots)
            return false;
        return !overrideChunkChanged || _renderWorld.AllActiveSlotsHaveOverride;
    }

    private static bool AreSourceSlotsDense(
        ReadOnlySpan<RenderSourceLink> links,
        ReadOnlySpan<EntityId> slotEntities,
        ReadOnlySpan<RenderInstance> slotInstances,
        ReadOnlySpan<bool> slotActive)
    {
        for (int i = 0; i < links.Length; i++)
        {
            if (!IsDenseSourceSlot(links[i], i, slotEntities, slotInstances, slotActive))
                return false;
        }

        return true;
    }

    private static bool IsDenseSourceSlot(
        RenderSourceLink link,
        int index,
        ReadOnlySpan<EntityId> slotEntities,
        ReadOnlySpan<RenderInstance> slotInstances,
        ReadOnlySpan<bool> slotActive)
        => link.InstanceIndex == index
            && link.RenderEntity != EntityId.Null
            && slotActive[index]
            && slotEntities[index] == link.RenderEntity
            && slotInstances[index].InstanceIndex == index;

    private bool ApplyFullRangeChunk(
        World renderWorld,
        ReadOnlySpan<RenderSourceLink> links,
        ReadOnlySpan<WorldTransform> transforms,
        ReadOnlySpan<MaterialOverride> overrides,
        ReadOnlySpan<uint> transformVersions,
        ReadOnlySpan<uint> overrideVersions,
        ReadOnlySpan<RenderInstance> slotInstances,
        ReadOnlySpan<MaterialOverride> slotOverrides,
        ReadOnlySpan<bool> slotHasOverrides,
        bool transformChunkChanged,
        bool overrideChunkChanged,
        InstanceDirtyFlags fullRangeDirty,
        out bool shapeChanged)
    {
        int changedRowCount = 0;
        bool changed = false;
        shapeChanged = false;
        using (Profiler.BeginScope("RenderWorldExtractor.UpdateInstanceChunk.FullRangeApply"))
        {
            for (int i = 0; i < links.Length; i++)
            {
                bool transformChanged = transformChunkChanged
                    && (int)(transformVersions[i] - _lastSourceVersion) > 0;
                bool overrideChanged = overrideChunkChanged
                    && (int)(overrideVersions[i] - _lastSourceVersion) > 0;
                if (!transformChanged && !overrideChanged)
                    continue;

                changedRowCount++;
                changed |= ApplyFullRangeRow(
                    renderWorld,
                    i,
                    links,
                    transforms,
                    overrides,
                    slotInstances,
                    slotOverrides,
                    slotHasOverrides,
                    transformChanged,
                    overrideChanged,
                    out bool rowShapeChanged);
                shapeChanged |= rowShapeChanged;
            }
        }

        AddFullRangeUpdates(
            links,
            transformVersions,
            overrideVersions,
            transformChunkChanged,
            overrideChunkChanged,
            changedRowCount,
            fullRangeDirty);
        return changed;
    }

    private bool ApplyFullRangeRow(
        World renderWorld,
        int row,
        ReadOnlySpan<RenderSourceLink> links,
        ReadOnlySpan<WorldTransform> transforms,
        ReadOnlySpan<MaterialOverride> overrides,
        ReadOnlySpan<RenderInstance> slotInstances,
        ReadOnlySpan<MaterialOverride> slotOverrides,
        ReadOnlySpan<bool> slotHasOverrides,
        bool transformChanged,
        bool overrideChanged,
        out bool shapeChanged)
    {
        shapeChanged = false;
        var renderEntity = links[row].RenderEntity;
        var instance = slotInstances[row];
        var dirty = InstanceDirtyFlags.None;
        bool changed = false;
        if (transformChanged)
            changed |= ApplyTransformChange(ref instance, transforms[row], ref dirty);

        bool hasOverrideAfter = false;
        var updateOverride = EmptyMaterialOverride();
        if (overrideChanged)
        {
            hasOverrideAfter = slotHasOverrides[row];
            updateOverride = slotOverrides[row];
            changed |= ApplyOverrideChange(
                renderWorld,
                renderEntity,
                overrides[row],
                markHeaderWhenNew: false,
                ref instance,
                ref hasOverrideAfter,
                ref updateOverride,
                ref dirty,
                out shapeChanged);
        }

        _renderWorld.UpdateInstanceSlot(row, in instance, hasOverrideAfter, in updateOverride, dirty);
        return changed;
    }

    private bool ApplySparseChunk(
        World renderWorld,
        ReadOnlySpan<RenderSourceLink> links,
        ReadOnlySpan<WorldTransform> transforms,
        ReadOnlySpan<MaterialOverride> overrides,
        ReadOnlySpan<uint> transformVersions,
        ReadOnlySpan<uint> overrideVersions,
        bool transformChunkChanged,
        bool overrideChunkChanged,
        out bool shapeChanged)
    {
        bool changed = false;
        shapeChanged = false;
        using (Profiler.BeginScope("RenderWorldExtractor.UpdateInstanceChunk.SparseApply"))
        {
            for (int i = 0; i < links.Length; i++)
            {
                changed |= ApplySparseRow(
                    renderWorld,
                    i,
                    links,
                    transforms,
                    overrides,
                    transformVersions,
                    overrideVersions,
                    transformChunkChanged,
                    overrideChunkChanged,
                    out bool rowShapeChanged);
                shapeChanged |= rowShapeChanged;
            }
        }

        return changed;
    }

    private bool ApplySparseRow(
        World renderWorld,
        int row,
        ReadOnlySpan<RenderSourceLink> links,
        ReadOnlySpan<WorldTransform> transforms,
        ReadOnlySpan<MaterialOverride> overrides,
        ReadOnlySpan<uint> transformVersions,
        ReadOnlySpan<uint> overrideVersions,
        bool transformChunkChanged,
        bool overrideChunkChanged,
        out bool shapeChanged)
    {
        shapeChanged = false;
        bool transformChanged = transformChunkChanged
            && (int)(transformVersions[row] - _lastSourceVersion) > 0;
        bool overrideChanged = overrideChunkChanged
            && (int)(overrideVersions[row] - _lastSourceVersion) > 0;
        if (!transformChanged && !overrideChanged)
            return false;

        var link = links[row];
        var renderEntity = link.RenderEntity;
        if (renderEntity == EntityId.Null)
            return false;

        var dirty = InstanceDirtyFlags.None;
        if (!_renderWorld.TryGetInstance(
            link.InstanceIndex,
            out var slotEntity,
            out var instance,
            out bool hasOverrideAfter,
            out var updateOverride))
        {
            return false;
        }

        if (slotEntity != renderEntity)
            return false;

        bool changed = false;
        if (transformChanged)
            changed |= ApplyTransformChange(ref instance, transforms[row], ref dirty);
        if (overrideChanged)
            changed |= ApplyOverrideChange(
                renderWorld,
                renderEntity,
                overrides[row],
                markHeaderWhenNew: true,
                ref instance,
                ref hasOverrideAfter,
                ref updateOverride,
                ref dirty,
                out shapeChanged);

        CommitSparseInstance(renderEntity, in instance, hasOverrideAfter, in updateOverride, dirty);
        return changed;
    }

    private static MaterialOverride EmptyMaterialOverride()
        => default;

    private static bool ApplyTransformChange(
        ref RenderInstance instance,
        WorldTransform transform,
        ref InstanceDirtyFlags dirty)
    {
        var gpuTransform = GpuTransform.FromQvvs(transform.Qvvs);
        instance.PrevTransform = instance.Transform;
        instance.Transform = gpuTransform;
        dirty |= InstanceDirtyFlags.Transform;
        return true;
    }

    private static bool ApplyOverrideChange(
        World renderWorld,
        EntityId renderEntity,
        MaterialOverride next,
        bool markHeaderWhenNew,
        ref RenderInstance instance,
        ref bool hasOverrideAfter,
        ref MaterialOverride updateOverride,
        ref InstanceDirtyFlags dirty,
        out bool shapeChanged)
    {
        bool hadOverride = hasOverrideAfter;
        dirty |= hadOverride || !markHeaderWhenNew
            ? InstanceDirtyFlags.Data
            : InstanceDirtyFlags.Header | InstanceDirtyFlags.Data;
        if ((instance.DataFlags & InstanceFlags.MaterialOverride) == 0)
        {
            instance.DataFlags |= InstanceFlags.MaterialOverride;
            if (markHeaderWhenNew)
                dirty |= InstanceDirtyFlags.Header;
        }

        if (!hadOverride && !renderWorld.Has<MaterialOverride>(renderEntity))
            renderWorld.Add(renderEntity, next);
        updateOverride = next;
        hasOverrideAfter = true;
        shapeChanged = !hadOverride;
        return true;
    }

    private void CommitSparseInstance(
        EntityId renderEntity,
        in RenderInstance instance,
        bool hasOverrideAfter,
        in MaterialOverride updateOverride,
        InstanceDirtyFlags dirty)
    {
        if (dirty == InstanceDirtyFlags.None)
            return;

        _renderWorld.UpdateInstanceSlot(
            instance.InstanceIndex,
            in instance,
            hasOverrideAfter,
            in updateOverride,
            dirty);
        _renderWorld.AddInstanceUpdate(
            renderEntity,
            instance.InstanceIndex,
            dirty);
    }

    private void AddFullRangeUpdates(
        ReadOnlySpan<RenderSourceLink> links,
        ReadOnlySpan<uint> transformVersions,
        ReadOnlySpan<uint> overrideVersions,
        bool transformChunkChanged,
        bool overrideChunkChanged,
        int changedRowCount,
        InstanceDirtyFlags fullRangeDirty)
    {
        using (Profiler.BeginScope("RenderWorldExtractor.UpdateInstanceChunk.FullRangeUpdates"))
        {
            if (changedRowCount == links.Length)
            {
                _renderWorld.AddInstanceUpdatesForAllSlots(
                    fullRangeDirty,
                    allDataHaveOverride: true);
                return;
            }

            AddChangedSlotUpdates(
                links,
                transformVersions,
                overrideVersions,
                transformChunkChanged,
                overrideChunkChanged);
        }
    }

    private void AddChangedSlotUpdates(
        ReadOnlySpan<RenderSourceLink> links,
        ReadOnlySpan<uint> transformVersions,
        ReadOnlySpan<uint> overrideVersions,
        bool transformChunkChanged,
        bool overrideChunkChanged)
    {
        for (int i = 0; i < links.Length; i++)
        {
            var dirty = BuildChangedDirty(
                i,
                transformChunkChanged,
                overrideChunkChanged,
                transformVersions,
                overrideVersions);
            if (dirty == InstanceDirtyFlags.None)
                continue;

            _renderWorld.AddInstanceUpdate(links[i].RenderEntity, i, dirty);
        }
    }

    private InstanceDirtyFlags BuildChangedDirty(
        int row,
        bool transformChunkChanged,
        bool overrideChunkChanged,
        ReadOnlySpan<uint> transformVersions,
        ReadOnlySpan<uint> overrideVersions)
    {
        var dirty = InstanceDirtyFlags.None;
        if (transformChunkChanged && (int)(transformVersions[row] - _lastSourceVersion) > 0)
            dirty |= InstanceDirtyFlags.Transform;
        if (overrideChunkChanged && (int)(overrideVersions[row] - _lastSourceVersion) > 0)
            dirty |= InstanceDirtyFlags.Data;
        return dirty;
    }
}

public sealed partial class RenderWorldExtractor
{
    private bool UpdateMeshes(World sourceWorld, uint current)
    {
        bool changed = false;
        var renderWorld = _renderWorld.World;
        foreach (var chunk in sourceWorld.RunQuery(_changedMesh, _lastSourceVersion, current).Chunks)
        {
            var meshVersions = chunk.ReadWriteVersions<MeshInstance>();
            var meshes = chunk.Read<MeshInstance>();
            var links = chunk.Read<RenderSourceLink>();
            for (int i = 0; i < links.Length; i++)
            {
                if ((int)(meshVersions[i] - _lastSourceVersion) <= 0)
                    continue;

                changed |= UpdateMeshRow(renderWorld, links[i], meshes[i]);
            }
        }

        return changed;
    }

    private bool UpdateMeshRow(World renderWorld, RenderSourceLink link, MeshInstance mesh)
    {
        var renderEntity = link.RenderEntity;
        if (!TryReadRenderSlot(
            renderWorld,
            renderEntity,
            link.InstanceIndex,
            out var currentInstance,
            out bool hasOverride,
            out var materialOverride))
        {
            return false;
        }

        var dirty = InstanceDirtyFlags.None;
        bool meshChanged = currentInstance.Mesh != mesh.Mesh;
        if (meshChanged)
            dirty |= InstanceDirtyFlags.Header | InstanceDirtyFlags.MaterialHeader;

        float bounds = MathF.Max(0f, mesh.BoundsExpansion);
        bool boundsChanged = currentInstance.BoundsExpansion != bounds;
        if (boundsChanged)
            dirty |= InstanceDirtyFlags.Header | InstanceDirtyFlags.MaterialHeader;

        if (dirty == InstanceDirtyFlags.None)
            return false;

        ref var instance = ref renderWorld.Get<RenderInstance>(renderEntity);
        if (meshChanged)
            instance.Mesh = mesh.Mesh;
        if (boundsChanged)
            instance.BoundsExpansion = bounds;
        _renderWorld.StoreInstance(renderEntity, in instance, hasOverride, in materialOverride);
        InstanceMarks.Mark(renderWorld, renderEntity, dirty);
        return true;
    }

    private bool TryReadRenderSlot(
        World renderWorld,
        EntityId renderEntity,
        int instanceIndex,
        out RenderInstance currentInstance,
        out bool hasOverride,
        out MaterialOverride materialOverride)
    {
        currentInstance = default;
        hasOverride = false;
        materialOverride = default;
        if (renderEntity == EntityId.Null)
            return false;
        if (!renderWorld.IsAlive(renderEntity))
            return false;
        if (!_renderWorld.TryGetInstance(
            instanceIndex,
            out var slotEntity,
            out currentInstance,
            out hasOverride,
            out materialOverride))
        {
            return false;
        }

        return slotEntity == renderEntity;
    }

    private bool UpdateBindings(World sourceWorld, uint current)
    {
        bool changed = false;
        var renderWorld = _renderWorld.World;
        foreach (var chunk in sourceWorld.RunQuery(_changedBindings, _lastSourceVersion, current).Chunks)
        {
            var bindingVersions = chunk.ReadWriteVersions<MeshMaterialBindings>();
            var bindings = chunk.Read<MeshMaterialBindings>();
            var links = chunk.Read<RenderSourceLink>();
            for (int i = 0; i < links.Length; i++)
            {
                if ((int)(bindingVersions[i] - _lastSourceVersion) <= 0)
                    continue;

                changed |= UpdateBindingRow(
                    renderWorld,
                    links[i].RenderEntity,
                    bindings[i].Materials.Span);
            }
        }

        return changed;
    }

    private static bool IsLiveRenderInstance(World renderWorld, EntityId renderEntity)
        => renderEntity != EntityId.Null
            && renderWorld.IsAlive(renderEntity)
            && renderWorld.Has<RenderInstance>(renderEntity);

    private static bool UpdateBindingRow(
        World renderWorld,
        EntityId renderEntity,
        ReadOnlySpan<Handle<Material>> incoming)
    {
        if (!IsLiveRenderInstance(renderWorld, renderEntity))
            return false;

        bool hasMaterials = renderWorld.Has<RenderMaterials>(renderEntity);
        if (hasMaterials
            && renderWorld.ReadRef<RenderMaterials>(renderEntity).Materials.Span.SequenceEqual(incoming))
        {
            return false;
        }

        var materials = incoming.ToArray();
        if (hasMaterials)
            renderWorld.Get<RenderMaterials>(renderEntity).Materials = materials;
        else
            renderWorld.Add(renderEntity, new RenderMaterials { Materials = materials });
        InstanceMarks.Mark(renderWorld, renderEntity, InstanceDirtyFlags.MaterialHeader);
        return true;
    }

    private bool RemoveBindings(World sourceWorld, uint current)
    {
        bool changed = false;
        var renderWorld = _renderWorld.World;
        foreach (var chunk in sourceWorld.RunQuery(_removedBindings, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                if (!TryResolveRenderEntity(chunk, row, out var renderEntity))
                    continue;

                changed |= ClearBindingRow(renderWorld, renderEntity);
            }
        }

        sourceWorld.ClearRemoved<MeshMaterialBindings>(current);
        return changed;
    }

    private bool TryResolveRenderEntity(QueryChunkView chunk, int row, out EntityId renderEntity)
    {
        var source = chunk.GetEntity(row);
        if (chunk.TryRead(row, out RenderSourceLink link))
        {
            renderEntity = link.RenderEntity;
            return true;
        }

        return _instanceEntities.TryGetValue(source, out renderEntity);
    }

    private static bool ClearBindingRow(World renderWorld, EntityId renderEntity)
    {
        if (!IsLiveRenderInstance(renderWorld, renderEntity))
            return false;

        if (renderWorld.Has<RenderMaterials>(renderEntity))
            renderWorld.Get<RenderMaterials>(renderEntity).Materials = ReadOnlyMemory<Handle<Material>>.Empty;
        else
            renderWorld.Add(renderEntity, new RenderMaterials { Materials = ReadOnlyMemory<Handle<Material>>.Empty });
        InstanceMarks.Mark(renderWorld, renderEntity, InstanceDirtyFlags.MaterialHeader);
        return true;
    }

    private bool RemoveOverrides(World sourceWorld, uint current, out bool shapeChanged)
    {
        bool changed = false;
        shapeChanged = false;
        var renderWorld = _renderWorld.World;
        foreach (var chunk in sourceWorld.RunQuery(_removedOverride, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                if (!TryResolveOverrideRemoval(chunk, row, out var renderEntity, out int instanceIndex))
                    continue;

                changed |= RemoveOverrideRow(
                    renderWorld,
                    renderEntity,
                    instanceIndex,
                    out bool rowShapeChanged);
                shapeChanged |= rowShapeChanged;
            }
        }

        sourceWorld.ClearRemoved<MaterialOverride>(current);
        return changed;
    }

    private bool TryResolveOverrideRemoval(
        QueryChunkView chunk,
        int row,
        out EntityId renderEntity,
        out int instanceIndex)
    {
        var source = chunk.GetEntity(row);
        if (chunk.TryRead(row, out RenderSourceLink link))
        {
            renderEntity = link.RenderEntity;
            instanceIndex = link.InstanceIndex;
            return true;
        }

        if (!_instanceEntities.TryGetValue(source, out renderEntity))
        {
            instanceIndex = 0;
            return false;
        }

        if (_renderWorld.World.TryRead(renderEntity, out RenderInstance staleInstance))
        {
            instanceIndex = staleInstance.InstanceIndex;
            return true;
        }

        instanceIndex = 0;
        return false;
    }

    private bool RemoveOverrideRow(
        World renderWorld,
        EntityId renderEntity,
        int instanceIndex,
        out bool shapeChanged)
    {
        shapeChanged = false;
        if (!TryReadRenderSlot(
            renderWorld,
            renderEntity,
            instanceIndex,
            out var currentInstance,
            out bool hasOverride,
            out _))
        {
            return false;
        }

        bool hasOverrideComponent = renderWorld.Has<MaterialOverride>(renderEntity);
        bool hadOverride = hasOverride
            || hasOverrideComponent
            || (currentInstance.DataFlags & InstanceFlags.MaterialOverride) != 0;
        if (!hadOverride && currentInstance.DataOffset == 0)
            return false;

        if (hasOverrideComponent)
            renderWorld.Remove<MaterialOverride>(renderEntity);
        ref var instance = ref renderWorld.Get<RenderInstance>(renderEntity);
        ClearOverrideData(ref instance);
        ClearOverrideData(ref currentInstance);
        _renderWorld.StoreInstance(renderEntity, in currentInstance, hasOverride: false, materialOverride: default);
        InstanceMarks.Mark(renderWorld, renderEntity, InstanceDirtyFlags.Header | InstanceDirtyFlags.Data);
        shapeChanged = hadOverride;
        return true;
    }

    private static void ClearOverrideData(ref RenderInstance instance)
    {
        instance.DataFlags &= ~InstanceFlags.MaterialOverride;
        instance.DataOffset = 0;
    }

    private void CapturePrevious()
    {
        _previous.Clear();
        ReadOnlySpan<RenderInstance> instances = _renderWorld.InstanceSlots;
        ReadOnlySpan<bool> active = _renderWorld.InstanceSlotActive;
        for (int i = 0; i < instances.Length; i++)
        {
            if (active[i])
                _previous[instances[i].SourceEntity] = instances[i];
        }
    }

    private void IndexInstances()
    {
        _instanceEntities.Clear();
        ReadOnlySpan<EntityId> entities = _renderWorld.InstanceSlotEntities;
        ReadOnlySpan<RenderInstance> instances = _renderWorld.InstanceSlots;
        ReadOnlySpan<bool> active = _renderWorld.InstanceSlotActive;
        for (int i = 0; i < instances.Length; i++)
        {
            if (active[i])
                _instanceEntities[instances[i].SourceEntity] = entities[i];
        }
    }

    private void EnsureQueries(World sourceWorld)
    {
        if (ReferenceEquals(_sourceWorld, sourceWorld))
            return;

        _sourceWorld = sourceWorld;
        CreateInstanceQueries(sourceWorld);
        CreateRemovalQueries(sourceWorld);
        CreateMaterialQueries(sourceWorld);
        CreateLightQueries(sourceWorld);
        ResetSourceTracking();
    }

    private void CreateInstanceQueries(World sourceWorld)
    {
        _sourceQuery = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Read<MeshInstance>()
                .Optional<MeshMaterialBindings>(QueryAccess.Read)
                .Optional<MaterialOverride>(QueryAccess.Read));
        _unlinkedSources = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Read<MeshInstance>()
                .None<RenderSourceLink>()
                .Optional<MeshMaterialBindings>(QueryAccess.Read)
                .Optional<MaterialOverride>(QueryAccess.Read));
        _changedTransforms = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Read<RenderSourceLink>()
                .Optional<MaterialOverride>(QueryAccess.Read)
                .ChunkChanged<WorldTransform>());
        _changedOverrides = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Read<RenderSourceLink>()
                .Read<MaterialOverride>()
                .ChunkChanged<MaterialOverride>());
    }

    private void CreateRemovalQueries(World sourceWorld)
    {
        _changedMesh = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<MeshInstance>()
                .Read<RenderSourceLink>()
                .ChunkChanged<MeshInstance>());
        _lostMesh = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<RenderSourceLink>()
                .None<MeshInstance>());
        _lostTransform = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<RenderSourceLink>()
                .None<WorldTransform>());
        _removedMesh = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Optional<RenderSourceLink>(QueryAccess.Read)
                .Removed<MeshInstance>());
        _removedTransform = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Optional<RenderSourceLink>(QueryAccess.Read)
                .Removed<WorldTransform>());
    }

    private void CreateMaterialQueries(World sourceWorld)
    {
        _changedBindings = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<MeshMaterialBindings>()
                .Read<RenderSourceLink>()
                .ChunkChanged<MeshMaterialBindings>());
        _removedBindings = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Optional<RenderSourceLink>(QueryAccess.Read)
                .Removed<MeshMaterialBindings>());
        _removedOverride = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Optional<RenderSourceLink>(QueryAccess.Read)
                .Removed<MaterialOverride>());
    }

    private void CreateLightQueries(World sourceWorld)
    {
        _sceneLights = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<SceneLights>());
        _addedSceneLights = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<SceneLights>()
                .Added<SceneLights>());
        _changedSceneLights = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<SceneLights>()
                .Changed<SceneLights>());
        _removedSceneLights = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Removed<SceneLights>());
    }

    private void ResetSourceTracking()
    {
        _lastSourceVersion = 0;
        _built = false;
        _nextIndex = 0;
        _denseSourceSlotShapeVersion = 0;
        _denseSourceSlots = false;
        _freeIndices.Clear();
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        _removedSources.Clear();
    }

    private int AllocateIndex()
    {
        int last = _freeIndices.Count - 1;
        if (last >= 0)
        {
            int index = _freeIndices[last];
            _freeIndices.RemoveAt(last);
            return index;
        }

        return _nextIndex++;
    }
}

public sealed partial class RenderWorldExtractor
{
    private bool CollectLights(World sourceWorld)
        => CollectLights(sourceWorld, out _);

    private bool ApplyLights(World sourceWorld, uint current, out bool shapeChanged)
    {
        if (!HasRows(sourceWorld, _addedSceneLights, current)
            && !HasRows(sourceWorld, _changedSceneLights, current)
            && !HasRows(sourceWorld, _removedSceneLights, current))
        {
            shapeChanged = false;
            return false;
        }

        bool changed = CollectLights(sourceWorld, out shapeChanged);
        sourceWorld.ClearRemoved<SceneLights>(current);
        return changed;
    }

    private bool HasRows(World sourceWorld, QueryHandle query, uint current)
    {
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(query, _lastSourceVersion, current).Chunks)
        {
            var rows = chunk.RowIndices;
            if (rows.MoveNext(out _))
                return true;
        }

        return false;
    }

    private bool CollectLights(World sourceWorld, out bool shapeChanged)
    {
        ClearLightBuffers();
        Handle<Texture> lightCookieAtlas = default;
        bool lightCookieAtlasSet = false;

        foreach (var chunk in sourceWorld.RunQuery(_sceneLights).Chunks)
        {
            var lights = chunk.Read<SceneLights>();
            for (int i = 0; i < lights.Length; i++)
                CollectSceneLights(lights[i], ref lightCookieAtlas, ref lightCookieAtlasSet);
        }

        shapeChanged = HasLightShapeChanged(lightCookieAtlas);

        if (AreLightsEqual(_renderWorld.SceneLights, _directionalLights, _pointLights, _spotLights, lightCookieAtlas))
            return false;

        _renderWorld.SceneLights = CreateSceneLights(lightCookieAtlas);
        return true;
    }

    private void ClearLightBuffers()
    {
        _directionalLights.Clear();
        _pointLights.Clear();
        _spotLights.Clear();
    }

    private void CollectSceneLights(
        SceneLights lights,
        ref Handle<Texture> lightCookieAtlas,
        ref bool lightCookieAtlasSet)
    {
        ApplyLightCookieAtlas(lights, ref lightCookieAtlas, ref lightCookieAtlasSet);
        AddDirectionalLights(lights.DirectionalLights.Span);
        AddPointLights(lights.PointLights.Span);
        AddSpotLights(lights.SpotLights.Span);
    }

    private static void ApplyLightCookieAtlas(
        SceneLights lights,
        ref Handle<Texture> lightCookieAtlas,
        ref bool lightCookieAtlasSet)
    {
        if (!lights.LightCookieAtlas.IsValid)
            return;
        if (lightCookieAtlasSet && lights.LightCookieAtlas != lightCookieAtlas)
        {
            throw new InvalidOperationException(
                "RenderWorld extraction supports one light cookie atlas per scene.");
        }

        lightCookieAtlas = lights.LightCookieAtlas;
        lightCookieAtlasSet = true;
    }

    private void AddDirectionalLights(ReadOnlySpan<DirectionalLight> lights)
    {
        for (int i = 0; i < lights.Length; i++)
            _directionalLights.Add(lights[i]);
    }

    private void AddPointLights(ReadOnlySpan<PointLight> lights)
    {
        for (int i = 0; i < lights.Length; i++)
            _pointLights.Add(lights[i]);
    }

    private void AddSpotLights(ReadOnlySpan<SpotLight> lights)
    {
        for (int i = 0; i < lights.Length; i++)
            _spotLights.Add(lights[i]);
    }

    private bool HasLightShapeChanged(Handle<Texture> lightCookieAtlas)
        => _renderWorld.SceneLights.DirectionalLights.Length != _directionalLights.Count
            || _renderWorld.SceneLights.PointLights.Length != _pointLights.Count
            || _renderWorld.SceneLights.SpotLights.Length != _spotLights.Count
            || _renderWorld.SceneLights.LightCookieAtlas != lightCookieAtlas;

    private SceneLights CreateSceneLights(Handle<Texture> lightCookieAtlas)
    {
        if (_directionalLights.Count == 0
            && _pointLights.Count == 0
            && _spotLights.Count == 0
            && !lightCookieAtlas.IsValid)
        {
            return default;
        }

        return new SceneLights(
            _directionalLights.ToArray(),
            _pointLights.ToArray(),
            _spotLights.ToArray(),
            lightCookieAtlas);
    }

    private void TouchShape()
    {
        _shapeVersion++;
        if (_shapeVersion == 0)
            _shapeVersion = 1;
        _renderWorld.ShapeVersion = _shapeVersion;
    }

    private static bool AreLightsEqual(
        in SceneLights current,
        List<DirectionalLight> directionalLights,
        List<PointLight> pointLights,
        List<SpotLight> spotLights,
        Handle<Texture> lightCookieAtlas)
        => AreLightsEqual(current.DirectionalLights.Span, directionalLights)
            && AreLightsEqual(current.PointLights.Span, pointLights)
            && AreLightsEqual(current.SpotLights.Span, spotLights)
            && current.LightCookieAtlas == lightCookieAtlas;

    private static bool AreLightsEqual(ReadOnlySpan<DirectionalLight> current, List<DirectionalLight> next)
    {
        if (current.Length != next.Count)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            DirectionalLight left = current[i];
            DirectionalLight right = next[i];
            if (left.Direction != right.Direction
                || left.Color != right.Color
                || left.Intensity != right.Intensity
                || left.LayerMask != right.LayerMask
                || left.CookieIndex != right.CookieIndex
                || left.CookieStrength != right.CookieStrength
                || left.CookieScaleOffset != right.CookieScaleOffset
                || left.WorldToLightCookie != right.WorldToLightCookie)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreLightsEqual(ReadOnlySpan<PointLight> current, List<PointLight> next)
    {
        if (current.Length != next.Count)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            PointLight left = current[i];
            PointLight right = next[i];
            if (left.Position != right.Position
                || left.Range != right.Range
                || left.Color != right.Color
                || left.Intensity != right.Intensity
                || left.LayerMask != right.LayerMask
                || left.CookieIndex != right.CookieIndex
                || left.CookieStrength != right.CookieStrength
                || left.CookieScaleOffset != right.CookieScaleOffset
                || left.WorldToLightCookie != right.WorldToLightCookie)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreLightsEqual(ReadOnlySpan<SpotLight> current, List<SpotLight> next)
    {
        if (current.Length != next.Count)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            if (!AreSpotLightsEqual(current[i], next[i]))
                return false;
        }

        return true;
    }

    private static bool AreSpotLightsEqual(SpotLight left, SpotLight right)
        => AreSpotLightShapeEqual(left, right)
            && AreSpotLightCookieEqual(left, right);

    private static bool AreSpotLightShapeEqual(SpotLight left, SpotLight right)
        => left.Position == right.Position
            && left.Range == right.Range
            && left.Direction == right.Direction
            && left.InnerConeCos == right.InnerConeCos
            && left.Color == right.Color
            && left.Intensity == right.Intensity
            && left.OuterConeCos == right.OuterConeCos
            && left.LayerMask == right.LayerMask;

    private static bool AreSpotLightCookieEqual(SpotLight left, SpotLight right)
        => left.CookieIndex == right.CookieIndex
            && left.CookieStrength == right.CookieStrength
            && left.CookieScaleOffset == right.CookieScaleOffset
            && left.WorldToLightCookie == right.WorldToLightCookie;

    private static bool SameTransform(in GpuTransform left, in GpuTransform right)
        => left.Rotation == right.Rotation
            && left.Position == right.Position
            && left.Scale == right.Scale
            && left.Stretch == right.Stretch
            && left.Padding == right.Padding;

    private void TrackLink(EntityId source, EntityId renderEntity, int instanceIndex)
    {
        _linkSources.Add(source);
        _linkTargets.Add(renderEntity);
        _linkIndices.Add(instanceIndex);
    }

    private void LinkSources(World sourceWorld)
    {
        for (int i = 0; i < _linkSources.Count; i++)
        {
            EntityId source = _linkSources[i];
            EntityId renderEntity = _linkTargets[i];
            int instanceIndex = _linkIndices[i];
            if (sourceWorld.IsAlive(source))
                sourceWorld.AddOrSet(
                    source,
                    new RenderSourceLink
                    {
                        RenderEntity = renderEntity,
                        InstanceIndex = instanceIndex,
                    });
        }

        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
    }

    private void ClearLinks(World sourceWorld)
    {
        for (int i = 0; i < _linkSources.Count; i++)
        {
            EntityId source = _linkSources[i];
            if (sourceWorld.IsAlive(source) && sourceWorld.Has<RenderSourceLink>(source))
                sourceWorld.Remove<RenderSourceLink>(source);
        }

        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
    }
}

