using Friflo.Engine.ECS;
using EasyEcs;

namespace SomeEngine.ECS.Comparison;

internal enum GrowthFeature { Core, Enabled, Changes, Buffer, Resources, Shared, Index, Sparse, Group }

internal abstract partial class ComparisonWorld
{
    internal GrowthFeature FeatureGrowth;
    internal bool GrowthPerformed;
    private int _growthRows, _growthExpectedRows;
    private long _growthTotal, _growthExpectedTotal, _growthReleases, _growthExpectedReleases;

    private void SelectGrowthFeature(string workload)
    {
        FeatureGrowth = workload switch
        {
            "enableable" => GrowthFeature.Enabled,
            "resources" => GrowthFeature.Resources,
            _ when workload.StartsWith("changes-") => GrowthFeature.Changes,
            _ when workload.StartsWith("buffer-") => GrowthFeature.Buffer,
            _ when workload.StartsWith("shared-") => GrowthFeature.Shared,
            _ when workload.StartsWith("index-") => GrowthFeature.Index,
            _ when workload.StartsWith("sparse-") => GrowthFeature.Sparse,
            _ when workload.StartsWith("group-") => GrowthFeature.Group,
            _ => GrowthFeature.Core,
        };
    }

    internal void BeginGrowth()
    {
        GrowthPerformed = true;
        _growthRows = 0;
        _growthTotal = _growthReleases = _growthExpectedReleases = 0;
        _growthExpectedRows = FeatureGrowth == GrowthFeature.Sparse ? Count / SparseStride
            : FeatureGrowth == GrowthFeature.Resources ? 16 : Count;
        _growthExpectedTotal = FeatureGrowth switch
        {
            GrowthFeature.Changes => (long)Count * (Count + 1),
            GrowthFeature.Buffer => Count * (3L * BufferLength - 1),
            GrowthFeature.Shared => (long)Count * SharedGroupCount + (long)Count * (SharedGroupCount - 1) / 2,
            GrowthFeature.Index or GrowthFeature.Group => Count / 16 * 120L + 16,
            GrowthFeature.Sparse => _growthExpectedRows + (long)SparseStride * _growthExpectedRows * (_growthExpectedRows - 1) / 2,
            GrowthFeature.Resources => 16,
            _ => (long)Count * (Count + 1) / 2,
        };
    }

    internal void ObserveGrowth(long value) { _growthRows++; _growthTotal += value; }
    internal void ObserveGrowth(int count, long total) { _growthRows += count; _growthTotal += total; }
    internal void ObserveGrowthOldValue(long value) => _growthTotal += value;
    internal void ObserveGrowthReleases(long actual, long expected)
        => (_growthReleases, _growthExpectedReleases) = (actual, expected);
    private bool CheckGrowthResult()
    {
        if (!GrowthPerformed || (_growthRows == _growthExpectedRows
            && _growthTotal == _growthExpectedTotal && _growthReleases == _growthExpectedReleases)) return true;
        throw new InvalidOperationException($"{GetType().Name}: {FeatureGrowth} growth rows={_growthRows}/{_growthExpectedRows}, total={_growthTotal}/{_growthExpectedTotal}, releases={_growthReleases}/{_growthExpectedReleases}.");
    }

    internal void GrowGrouping<TEntity, TPartition, TBackend>(
        ReferenceGrouping<TEntity, TPartition, TBackend> groups, TEntity[] created)
        where TEntity : struct where TPartition : class, IGroupPartition
        where TBackend : struct, IGroupingBackend<TEntity, TPartition>
    {
        var handles = new GroupEntity[created.Length];
        for (int i = 0; i < created.Length; i++)
        {
            handles[i] = groups.Add(created[i]);
            groups.Set(handles[i], GroupGrowthValues[i]);
        }
        foreach (var entity in handles) ObserveGrowth(groups.Read(entity).Value);
        foreach (var entity in handles) groups.Destroy(entity);
    }
}

internal sealed partial class SomeEngineWorld
{
    private bool GrowResources(Action observe)
    {
        if (FeatureGrowth != GrowthFeature.Resources) return false;
        BeginGrowth();
        long before = _resourceCounter!.Count;
        var access = new SomeResourceActions(_world, _resourceCounter);
        long total = ResourceSequence.Grow(ref access);
        ObserveGrowth(16, total);
        ObserveGrowthReleases(_resourceCounter.Count - before, 32);
        observe();
        return true;
    }

    private bool GrowFeatureValues(SomeEngine.ECS.Entities.Entity[] created)
    {
        if (FeatureGrowth == GrowthFeature.Core) return false;
        BeginGrowth();
        long releases = SparseCounter.Count;

        switch (FeatureGrowth)
        {
            case GrowthFeature.Enabled:
                for (int i = 0; i < created.Length; i++)
                {
                    if (i % 4 == 0) _world.Disable<Signal>(created[i]);
                }
                foreach (var entity in created) ObserveGrowth(_world.Read<Signal>(entity).Value);
                break;
            case GrowthFeature.Changes:
                foreach (var entity in created) ObserveGrowth(_world.Read<ScoreValue>(entity).Value);
                _world.Reserve<Position, Velocity, Health, ScoreValue, Removed<ScoreValue>>(created.Length - 1);
                _world.Reserve<Position, Velocity, Health, ScoreValue, Removed<ScoreValue>, GrowthMarker>(1);
                foreach (var entity in created) _world.Remove<ScoreValue>(entity);
                foreach (var entity in created) ObserveGrowthOldValue(_world.Read<Removed<ScoreValue>>(entity).Value.Value);
                _world.ClearRemoved<ScoreValue>();
                break;
            case GrowthFeature.Buffer:
                foreach (var entity in created)
                {
                    using var buffer = _world.GetBuffer<BufferItem>(entity);
                    buffer.EnsureCapacity(BufferLength * 2);
                    buffer.AddRange(_bufferInput);
                    buffer.AddRange(_bufferInput);
                }
                foreach (var entity in created)
                {
                    using var buffer = _world.ReadBuffer<BufferItem>(entity);
                    ObserveGrowth(buffer.Count + buffer[buffer.Count - 1].Value);
                }
                break;
            case GrowthFeature.Shared:
                foreach (var entity in created)
                    ObserveGrowth(_world.ReadShared<SharedNumber>(entity).Value);
                break;
            case GrowthFeature.Index:
                foreach (var entity in created) ObserveGrowth(_world.Read<IndexedNumber>(entity).Value);
                break;
            case GrowthFeature.Sparse:
                for (int i = 0; i < created.Length; i += SparseStride)
                {
                    _world.SetSparse(created[i], new SparseNumber(SparseCounter, i + 1, Count + i));
                    if (i % (SparseStride * 2) == 0) _world.SetSparse(created[i], new SparseWeight(Count + i));
                }
                for (int i = 0; i < created.Length; i += SparseStride)
                    ObserveGrowth(_world.ReadSparse<SparseNumber>(created[i]).Value);
                break;
            case GrowthFeature.Group:
                _world.Set<GroupedNumber>(created, GroupGrowthValues);
                foreach (var entity in created) ObserveGrowth(_world.Read<GroupedNumber>(entity).Value);
                break;
        }
        _world.Destroy(created);
        if (FeatureGrowth == GrowthFeature.Sparse)
            ObserveGrowthReleases(SparseCounter.Count - releases, created.Length / SparseStride);
        return true;
    }
}

internal sealed partial class EasyEcsWorld
{
    private bool GrowResources(Action observe)
    {
        if (FeatureGrowth != GrowthFeature.Resources) return false;
        BeginGrowth();
        long before = _resourceCounter!.Count;
        var access = new EasyResourceActions(_world, _resourceCounter);
        long total = ResourceSequence.Grow(ref access);
        ObserveGrowth(16, total);
        ObserveGrowthReleases(_resourceCounter.Count - before, 32);
        observe();
        return true;
    }

    private bool GrowFeatureValues(EasyEcs.Entity[] created)
    {
        if (FeatureGrowth == GrowthFeature.Core) return false;
        BeginGrowth();
        long releases = SparseCounter.Count;

        switch (FeatureGrowth)
        {
            case GrowthFeature.Enabled:
                for (int i = 0; i < created.Length; i++)
                {
                    created[i].Set(new Signal { Value = i + 1 });
                    if (i % 4 == 0) created[i].Set(new DisabledSignal());
                }
                foreach (var entity in created) ObserveGrowth(entity.Read<Signal>().Value);
                break;
            case GrowthFeature.Changes:
                for (int i = 0; i < created.Length; i++) created[i].Set(new EasyScore { Value = new ScoreValue { Value = i + 1 }, Added = ++_scoreVersion });
                foreach (var entity in created) ObserveGrowth(entity.Read<EasyScore>().Value.Value);
                _world.EnsureCapacity<Position, Velocity, Health, OldScore>(created.Length - 1);
                _world.EnsureCapacity<Position, Velocity, Health, OldScore, GrowthMarker>(1);
                _world.EnsureCapacity<Position, Velocity, Health, EasyScore, OldScore>(1);
                _world.EnsureCapacity<Position, Velocity, Health, EasyScore, OldScore, GrowthMarker>(1);
                foreach (var entity in created)
                {
                    entity.Set(new OldScore { Value = entity.Read<EasyScore>().Value, Version = ++_scoreVersion });
                    entity.Remove<EasyScore>();
                }
                foreach (var entity in created) ObserveGrowthOldValue(entity.Read<OldScore>().Value.Value);
                foreach (var entity in created) entity.Remove<OldScore>();
                break;
            case GrowthFeature.Buffer:
                foreach (var entity in created)
                {
                    entity.Set(new EasyEcs.DynamicBuffer<BufferItem>());
                    ref var buffer = ref entity.Get<EasyEcs.DynamicBuffer<BufferItem>>();
                    buffer.EnsureCapacity(BufferLength);
                    buffer.AddRange(_bufferInput);
                    buffer.AddRange(_bufferInput);
                }
                foreach (var entity in created)
                {
                    ref readonly var buffer = ref entity.Read<EasyEcs.DynamicBuffer<BufferItem>>();
                    ObserveGrowth(buffer.Count + buffer.AsReadOnlySpan()[buffer.Count - 1].Value);
                }
                break;
            case GrowthFeature.Shared:
                for (int i = 0; i < created.Length; i++)
                {
                    int id = _sharedValues!.Intern(new SharedNumber(SharedGroupCount + i % SharedGroupCount));
                    created[i].Set(new SharedNumberId(id));
                }
                foreach (var entity in created)
                    ObserveGrowth(_sharedValues!.Values[entity.Read<SharedNumberId>().Value].Value);
                break;
            case GrowthFeature.Index:
                for (int i = 0; i < created.Length; i++)
                {
                    created[i].Set(new IndexedNumber(i == 0 ? 16 : i % 16, Count + i));
                }
                foreach (var entity in created) ObserveGrowth(entity.Read<IndexedNumber>().Value);
                break;
            case GrowthFeature.Sparse:
                for (int i = 0; i < created.Length; i += SparseStride)
                {
                    _sparseValues!.Set(created[i], new SparseNumber(SparseCounter, i + 1, Count + i));
                    if (i % (SparseStride * 2) == 0) _sparseValues!.Set(created[i], new SparseWeight(Count + i));
                }
                for (int i = 0; i < created.Length; i += SparseStride)
                    ObserveGrowth(_sparseValues!.Read<SparseNumber>(created[i]).Value);
                break;
            case GrowthFeature.Group:
                for (int i = 0; i < created.Length; i++)
                    created[i].Set(GroupGrowthValues[i]);
                foreach (var entity in created) ObserveGrowth(entity.Read<GroupedNumber>().Value);
                break;
        }
        if (FeatureGrowth == GrowthFeature.Sparse)
        {
            for (int i = 0; i < created.Length; i++)
                if (i % SparseStride == 0) _sparseValues!.Destroy(created[i]);
                else created[i].Destroy();
            ObserveGrowthReleases(SparseCounter.Count - releases, created.Length / SparseStride);
            return true;
        }
        foreach (var entity in created) entity.Destroy();
        return true;
    }
}

internal sealed partial class FrifloWorld
{
    private bool GrowResources(Action observe)
    {
        if (FeatureGrowth != GrowthFeature.Resources) return false;
        BeginGrowth();
        long before = _resourceCounter!.Count;
        var access = new ReferenceResourceActions(_resourceValues!, _resourceCounter);
        long total = ResourceSequence.Grow(ref access);
        ObserveGrowth(16, total);
        ObserveGrowthReleases(_resourceCounter.Count - before, 32);
        observe();
        return true;
    }

    private bool GrowFeatureValues(Friflo.Engine.ECS.Entity[] created)
    {
        if (FeatureGrowth == GrowthFeature.Core) return false;
        BeginGrowth();
        long releases = SparseCounter.Count;
        if (FeatureGrowth == GrowthFeature.Group)
        {
            GrowGrouping(_groupedValues!, created);
            return true;
        }
        switch (FeatureGrowth)
        {
            case GrowthFeature.Enabled:
                for (int i = 0; i < created.Length; i++)
                {
                    created[i].AddComponent(new Signal { Value = i + 1 });
                    if (i % 4 == 0) created[i].AddComponent(new DisabledSignal());
                }
                foreach (var entity in created) ObserveGrowth(entity.GetComponent<Signal>().Value);
                break;
            case GrowthFeature.Changes:
                for (int i = 0; i < created.Length; i++) created[i].AddComponent(new ScoreHistory { Value = new ScoreValue { Value = i + 1 }, Added = ++_scoreVersion, Written = _scoreVersion });
                foreach (var entity in created) ObserveGrowth(entity.GetComponent<ScoreHistory>().Value.Value);
                _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, OldScore>(), default).EnsureCapacity(created.Length - 1);
                _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, OldScore, GrowthMarker>(), default).EnsureCapacity(1);
                _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, ScoreHistory, OldScore>(), default).EnsureCapacity(1);
                _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, ScoreHistory, OldScore, GrowthMarker>(), default).EnsureCapacity(1);
                foreach (var entity in created)
                {
                    entity.AddComponent(new OldScore { Value = entity.GetComponent<ScoreHistory>().Value, Version = ++_scoreVersion });
                    entity.RemoveComponent<ScoreHistory>();
                }
                foreach (var entity in created) ObserveGrowthOldValue(entity.GetComponent<OldScore>().Value.Value);
                foreach (var entity in created) entity.RemoveComponent<OldScore>();
                break;
            case GrowthFeature.Buffer:
                foreach (var entity in created)
                {
                    var data = _bufferPool!.Rent(BufferLength);
                    data.AddRange(_bufferInput!);
                    data.AddRange(_bufferInput!);
                    entity.AddComponent(new ReferenceBuffer { Data = data, Written = ++_bufferVersion });
                }
                foreach (var entity in created)
                {
                    var data = entity.GetComponent<ReferenceBuffer>().Data;
                    ObserveGrowth(data.Count + data.Items[data.Count - 1].Value);
                }
                break;
            case GrowthFeature.Shared:
                for (int i = 0; i < created.Length; i++)
                {
                    int id = _sharedValues!.Intern(new SharedNumber(SharedGroupCount + i % SharedGroupCount));
                    created[i].AddComponent(new SharedNumberId(id));
                }
                foreach (var entity in created)
                    ObserveGrowth(_sharedValues!.Values[entity.GetComponent<SharedNumberId>().Value].Value);
                break;
            case GrowthFeature.Index:
                for (int i = 0; i < created.Length; i++)
                {
                    var value = ReferenceIndexedNumber.Project(new IndexedNumber(i == 0 ? 16 : i % 16, Count + i));
                    created[i].AddComponent(value);
                }
                foreach (var entity in created) ObserveGrowth(entity.GetComponent<ReferenceIndexedNumber>().Key);
                break;
            case GrowthFeature.Sparse:
                for (int i = 0; i < created.Length; i += SparseStride)
                {
                    _sparseValues!.Set(created[i], new SparseNumber(SparseCounter, i + 1, Count + i));
                    if (i % (SparseStride * 2) == 0) _sparseValues!.Set(created[i], new SparseWeight(Count + i));
                }
                for (int i = 0; i < created.Length; i += SparseStride)
                    ObserveGrowth(_sparseValues!.Read<SparseNumber>(created[i]).Value);
                break;
        }
        if (FeatureGrowth == GrowthFeature.Sparse)
        {
            for (int i = 0; i < created.Length; i++)
                if (i % SparseStride == 0) _sparseValues!.Destroy(created[i]);
                else created[i].DeleteEntity();
            ObserveGrowthReleases(SparseCounter.Count - releases, created.Length / SparseStride);
            return true;
        }
        if (FeatureGrowth == GrowthFeature.Buffer)
            foreach (var entity in created) _bufferPool!.Return(entity.GetComponent<ReferenceBuffer>().Data);
        foreach (var entity in created) entity.DeleteEntity();
        return true;
    }
}

internal sealed partial class ArchWorld
{
    private bool GrowResources(Action observe)
    {
        if (FeatureGrowth != GrowthFeature.Resources) return false;
        BeginGrowth();
        long before = _resourceCounter!.Count;
        var access = new ReferenceResourceActions(_resourceValues!, _resourceCounter);
        long total = ResourceSequence.Grow(ref access);
        ObserveGrowth(16, total);
        ObserveGrowthReleases(_resourceCounter.Count - before, 32);
        observe();
        return true;
    }

    private bool GrowFeatureValues(Arch.Core.Entity[] created)
    {
        if (FeatureGrowth == GrowthFeature.Core) return false;
        BeginGrowth();
        long releases = SparseCounter.Count;
        if (FeatureGrowth == GrowthFeature.Group)
        {
            GrowGrouping(_groupedValues!, created);
            return true;
        }
        switch (FeatureGrowth)
        {
            case GrowthFeature.Enabled:
                for (int i = 0; i < created.Length; i++)
                {
                    _world.Add(created[i], new Signal { Value = i + 1 });
                    if (i % 4 == 0) _world.Add(created[i], new DisabledSignal());
                }
                foreach (var entity in created) ObserveGrowth(_world.Get<Signal>(entity).Value);
                break;
            case GrowthFeature.Changes:
                for (int i = 0; i < created.Length; i++) _world.Add(created[i], new ScoreHistory { Value = new ScoreValue { Value = i + 1 }, Added = ++_scoreVersion, Written = _scoreVersion });
                foreach (var entity in created) ObserveGrowth(_world.Get<ScoreHistory>(entity).Value.Value);
                _world.EnsureCapacity(new Arch.Core.Signature(Arch.Core.Component<Position>.ComponentType,
                    Arch.Core.Component<Velocity>.ComponentType, Arch.Core.Component<Health>.ComponentType,
                    Arch.Core.Component<OldScore>.ComponentType), created.Length - 1);
                _world.EnsureCapacity(new Arch.Core.Signature(Arch.Core.Component<Position>.ComponentType,
                    Arch.Core.Component<Velocity>.ComponentType, Arch.Core.Component<Health>.ComponentType,
                    Arch.Core.Component<OldScore>.ComponentType, Arch.Core.Component<GrowthMarker>.ComponentType), 1);
                _world.EnsureCapacity(new Arch.Core.Signature(Arch.Core.Component<Position>.ComponentType,
                    Arch.Core.Component<Velocity>.ComponentType, Arch.Core.Component<Health>.ComponentType,
                    Arch.Core.Component<ScoreHistory>.ComponentType, Arch.Core.Component<OldScore>.ComponentType), 1);
                _world.EnsureCapacity(new Arch.Core.Signature(Arch.Core.Component<Position>.ComponentType,
                    Arch.Core.Component<Velocity>.ComponentType, Arch.Core.Component<Health>.ComponentType,
                    Arch.Core.Component<ScoreHistory>.ComponentType, Arch.Core.Component<OldScore>.ComponentType,
                    Arch.Core.Component<GrowthMarker>.ComponentType), 1);
                foreach (var entity in created)
                {
                    _world.Add(entity, new OldScore { Value = _world.Get<ScoreHistory>(entity).Value, Version = ++_scoreVersion });
                    _world.Remove<ScoreHistory>(entity);
                }
                foreach (var entity in created) ObserveGrowthOldValue(_world.Get<OldScore>(entity).Value.Value);
                foreach (var entity in created) _world.Remove<OldScore>(entity);
                break;
            case GrowthFeature.Buffer:
                foreach (var entity in created)
                {
                    var data = _bufferPool!.Rent(BufferLength);
                    data.AddRange(_bufferInput!);
                    data.AddRange(_bufferInput!);
                    _world.Add(entity, new ReferenceBuffer { Data = data, Written = ++_bufferVersion });
                }
                foreach (var entity in created)
                {
                    var data = _world.Get<ReferenceBuffer>(entity).Data;
                    ObserveGrowth(data.Count + data.Items[data.Count - 1].Value);
                }
                break;
            case GrowthFeature.Shared:
                for (int i = 0; i < created.Length; i++)
                {
                    int id = _sharedValues!.Intern(new SharedNumber(SharedGroupCount + i % SharedGroupCount));
                    _world.Add(created[i], new SharedNumberId(id));
                    _sharedGroups!.Set(created[i], id);
                }
                foreach (var entity in created)
                    ObserveGrowth(_sharedValues!.Values[_world.Get<SharedNumberId>(entity).Value].Value);
                break;
            case GrowthFeature.Index:
                for (int i = 0; i < created.Length; i++)
                {
                    var value = ReferenceIndexedNumber.Project(new IndexedNumber(i == 0 ? 16 : i % 16, Count + i));
                    _world.Add(created[i], value);
                    _numberIndex!.Set(created[i], value.Key);
                }
                foreach (var entity in created) ObserveGrowth(_world.Get<ReferenceIndexedNumber>(entity).Key);
                break;
            case GrowthFeature.Sparse:
                for (int i = 0; i < created.Length; i += SparseStride)
                {
                    _sparseValues!.Set(created[i], new SparseNumber(SparseCounter, i + 1, Count + i));
                    if (i % (SparseStride * 2) == 0) _sparseValues!.Set(created[i], new SparseWeight(Count + i));
                }
                for (int i = 0; i < created.Length; i += SparseStride)
                    ObserveGrowth(_sparseValues!.Read<SparseNumber>(created[i]).Value);
                break;
        }
        if (FeatureGrowth == GrowthFeature.Sparse)
        {
            for (int i = 0; i < created.Length; i++)
                if (i % SparseStride == 0) _sparseValues!.Destroy(created[i]);
                else _world.Destroy(created[i]);
            ObserveGrowthReleases(SparseCounter.Count - releases, created.Length / SparseStride);
            return true;
        }
        if (FeatureGrowth == GrowthFeature.Buffer)
            foreach (var entity in created) _bufferPool!.Return(_world.Get<ReferenceBuffer>(entity).Data);
        if (FeatureGrowth == GrowthFeature.Index)
            foreach (var entity in created) _numberIndex!.Remove(entity);
        if (FeatureGrowth == GrowthFeature.Shared)
            foreach (var entity in created) _sharedGroups!.Remove(entity);
        foreach (var entity in created) _world.Destroy(entity);
        return true;
    }
}

internal sealed partial class DefaultEcsWorld
{
    private bool GrowResources(Action observe)
    {
        if (FeatureGrowth != GrowthFeature.Resources) return false;
        BeginGrowth();
        long before = _resourceCounter!.Count;
        var access = new DefaultResourceActions(_world, _resourceCounter);
        long total = ResourceSequence.Grow(ref access);
        ObserveGrowth(16, total);
        ObserveGrowthReleases(_resourceCounter.Count - before, 32);
        observe();
        return true;
    }

    private bool GrowFeatureValues(DefaultEcs.Entity[] created)
    {
        if (FeatureGrowth == GrowthFeature.Core) return false;
        BeginGrowth();
        long releases = SparseCounter.Count;
        if (FeatureGrowth == GrowthFeature.Group)
        {
            GrowGrouping(_groupedValues!, created);
            return true;
        }
        switch (FeatureGrowth)
        {
            case GrowthFeature.Enabled:
                for (int i = 0; i < created.Length; i++)
                {
                    created[i].Set(new Signal { Value = i + 1 });
                    if (i % 4 == 0) created[i].Set(new DisabledSignal());
                }
                foreach (var entity in created) ObserveGrowth(entity.Get<Signal>().Value);
                break;
            case GrowthFeature.Changes:
                for (int i = 0; i < created.Length; i++) created[i].Set(new ScoreHistory { Value = new ScoreValue { Value = i + 1 }, Added = ++_scoreVersion, Written = _scoreVersion });
                foreach (var entity in created) ObserveGrowth(entity.Get<ScoreHistory>().Value.Value);
                foreach (var entity in created)
                {
                    entity.Set(new OldScore { Value = entity.Get<ScoreHistory>().Value, Version = ++_scoreVersion });
                    entity.Remove<ScoreHistory>();
                }
                foreach (var entity in created) ObserveGrowthOldValue(entity.Get<OldScore>().Value.Value);
                foreach (var entity in created) entity.Remove<OldScore>();
                break;
            case GrowthFeature.Buffer:
                foreach (var entity in created)
                {
                    var data = _bufferPool!.Rent(BufferLength);
                    data.AddRange(_bufferInput!);
                    data.AddRange(_bufferInput!);
                    entity.Set(new ReferenceBuffer { Data = data, Written = ++_bufferVersion });
                }
                foreach (var entity in created)
                {
                    var data = entity.Get<ReferenceBuffer>().Data;
                    ObserveGrowth(data.Count + data.Items[data.Count - 1].Value);
                }
                break;
            case GrowthFeature.Shared:
                for (int i = 0; i < created.Length; i++)
                {
                    int id = _sharedValues!.Intern(new SharedNumber(SharedGroupCount + i % SharedGroupCount));
                    created[i].Set(new SharedNumberId(id));
                }
                foreach (var entity in created)
                    ObserveGrowth(_sharedValues!.Values[entity.Get<SharedNumberId>().Value].Value);
                break;
            case GrowthFeature.Index:
                for (int i = 0; i < created.Length; i++)
                {
                    var value = ReferenceIndexedNumber.Project(new IndexedNumber(i == 0 ? 16 : i % 16, Count + i));
                    created[i].Set(value);
                }
                foreach (var entity in created) ObserveGrowth(entity.Get<ReferenceIndexedNumber>().Key);
                break;
            case GrowthFeature.Sparse:
                for (int i = 0; i < created.Length; i += SparseStride)
                {
                    _sparseValues!.Set(created[i], new SparseNumber(SparseCounter, i + 1, Count + i));
                    if (i % (SparseStride * 2) == 0) _sparseValues!.Set(created[i], new SparseWeight(Count + i));
                }
                for (int i = 0; i < created.Length; i += SparseStride)
                    ObserveGrowth(_sparseValues!.Read<SparseNumber>(created[i]).Value);
                break;
        }
        if (FeatureGrowth == GrowthFeature.Sparse)
        {
            for (int i = 0; i < created.Length; i++)
                if (i % SparseStride == 0) _sparseValues!.Destroy(created[i]);
                else created[i].Dispose();
            ObserveGrowthReleases(SparseCounter.Count - releases, created.Length / SparseStride);
            return true;
        }
        if (FeatureGrowth == GrowthFeature.Buffer)
            foreach (var entity in created) _bufferPool!.Return(entity.Get<ReferenceBuffer>().Data);
        foreach (var entity in created) entity.Dispose();
        return true;
    }
}
