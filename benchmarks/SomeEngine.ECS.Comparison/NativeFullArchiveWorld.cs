using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class NativeFullArchiveWorld<TValues> : FullArchiveWorld, INativeArchiveProgress
    where TValues : struct, INativeArchiveValues
{
    private const ulong InitialColumns = (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 5) | (1UL << 6)
        | (1UL << 7) | (1UL << 8) | (1UL << 9) | (1UL << 10) | (1UL << 12);
    internal readonly NativeArchiveStore Store;
    private readonly TValues _values;
    internal readonly Entity[] Entities, Edges, TreeEntities;
    internal readonly Entity FirstRoot, SecondRoot, Dead;
    internal readonly SaveSystemVersions Systems = new();
    internal readonly NativeArchiveSerializer Serializer;
    private readonly ReferenceHierarchy<Entity, TValues> _hierarchy;
    private readonly ReferenceRelations<Entity, Entity, TValues, SaveEdge> _relations;
    private readonly ReferenceSparseStores<Entity, NativeArchiveValues> _sparse;
    private ReferenceSharedValues<SaveShared> _shared = new();
    private ReferenceEntityIndex<Entity> _index, _groups;
    private readonly ReferenceResources _resources = new();
    private readonly ResourceCounter _resourceCounter = new();
    internal SaveResource InitialResource;
    private readonly ReferenceVersionClock _clock = new();
    private ulong _destroyedPosition;
    private MixedLifecycleJobs? _lifecycleJobs;
    private bool _destroyedCompleted;
    public override ulong Version => _clock.Value;
    internal override Position ReadPosition(int ordinal) => _values.Read<Position>(Entities[ordinal]);
    internal override long ResourceReleases => _resourceCounter.Count;
    internal override bool InitialResourceReleased => InitialResource.Released;
    ulong INativeArchiveProgress.Version { get => Version; set => _clock.Value = value; }
    public void CompleteSystems() => Systems.Complete();

    internal NativeFullArchiveWorld(TValues values, int count, bool raw)
    {
        NativeArchiveStore store = values.Store; _values = values; Store = store; Entities = new Entity[count]; Edges = new Entity[count]; TreeEntities = new Entity[count + 2];
        NativeArchiveType[] primary = store.RowTypes = PrimaryTypes(raw);
        _index = store.HasNativeIndexes ? null! : new(count * 2 + 16, e => e.Id);
        _groups = store.HasNativeIndexes ? null! : new(count * 2 + 16, e => e.Id);
        _sparse = new(new(store), count * 2 + 16);
        for (int i = 0; i < count; i++)
        {
            ulong columns = InitialColumns | (i % 2 == 0 ? 1UL << 3 : 0) | (i % 4 == 0 ? 1UL << 4 : 0);
            Entity entity = Entities[i] = TreeEntities[i] = _values.Create(columns);
            _values.Set(entity, new Position { X = i }); _values.Set(entity, new Velocity { X = 1, Y = 2 });
            _values.Set(entity, new Health { Value = 100, Max = 100 });
            if (i % 2 == 0) _values.Set(entity, new Team()); if (i % 4 == 0) _values.Set(entity, new Sleeping());
            _values.Set(entity, new SaveCounter(i + 2, i)); _values.Set(entity, new SaveStep(2));
            _values.Set(entity, new SaveNativeFlags(i % 4 == 0));
            _values.Set(entity, new SaveNativeVersions(++_clock.Value, Version));
            _values.Set(entity, new IndexedNumber(i % 16, i)); if (!store.HasNativeIndexes) _index.Set(entity, i % 16);
            int shared = _shared.Intern(new(i % 16)); _values.Set(entity, new SharedNumberId(shared));
            if (!store.HasNativeIndexes) _groups.Set(entity, i % 16);
        }
        for (int i = 0; i < count; i++)
        {
            Entity entity = Entities[i], next = Entities[(i + 1) % count];
            _values.Set(entity, new SaveNativeBuffer { Items = NativeFullArchiveWorld.Links(entity, next, i), Added = ++_clock.Value, Written = ++_clock.Value });
            if (i % 4 == 0) _sparse.Set(entity, new SaveSparseRow(new(next, i * 7L), ++_clock.Value, Version));
            if (i % 100 == 0)
            {
                _values.Set(entity, new SaveNativeRemoved(_values.Read<SaveCounter>(entity), ++_clock.Value)); _values.Remove<SaveCounter>(entity);
                var buffer = _values.Read<SaveNativeBuffer>(entity);
                _values.Set(entity, new SaveNativeOldBuffer { Items = buffer.Items, Version = ++_clock.Value }); _values.Remove<SaveNativeBuffer>(entity);
            }
        }
        FirstRoot = TreeEntities[count] = _values.Create(); SecondRoot = TreeEntities[count + 1] = _values.Create();
        _hierarchy = new(values, TreeEntities, _clock); _relations = new(values, count * 2 + 16, _clock);
        var parents = new HierarchyEdit<Entity>[count];
        for (int i = 0; i < count; i++) parents[i] = new(Entities[i], FirstRoot);
        _hierarchy.SetParents(parents, false); _hierarchy.SetOrdered(FirstRoot); _hierarchy.SetOrdered(SecondRoot);
        _hierarchy.Reorder(FirstRoot, Entities[^1]);
        for (int i = 0; i < count; i++) Edges[i] = _relations.CreateEdge(FirstRoot, Entities[i], i);
        _relations.SetOrdered(FirstRoot); _relations.SetOrdered(SecondRoot); _relations.Reorder(FirstRoot, Edges[^1], false);
        var deferred = new HierarchyEdit<Entity>[count / 4]; var retargets = new RelationEdit<Entity>[count / 4];
        for (int i = 0; i < deferred.Length; i++)
        { deferred[i] = new(Entities[i * 4], SecondRoot); retargets[i] = new(Edges[i * 4], SecondRoot, Entities[i * 4]); }
        _hierarchy.SetParents(deferred, true); _relations.Retarget(retargets, true);
        Dead = _values.Create(); _values.Destroy(Dead);
        InitialResource = new(); for (int i = 0; i < InitialResource.Items.Length; i++) InitialResource.Items[i] = i * 13L;
        _resources.Set(InitialResource);
        ArchiveResources.Initialize(new NativeArchiveResources(_resources, _resourceCounter));
        Serializer = new(CreateTypes(primary), raw);
    }

    private static NativeArchiveType[] CreateTypes(NativeArchiveType[] primary)
    {
        var types = primary.ToList();
        types.Add(new NativeArchiveMetadataType(SaveSchema.Key(22, "Sparse"), typeof(SaveLink), typeof(SaveSchema.LinkCodec)));
        types.Add(new NativeArchiveMetadataType(SaveSchema.Key(23, "Resource"), typeof(SaveResource), typeof(SaveSchema.ResourceCodec)));
        types.Add(new NativeArchiveMetadataType(SaveSchema.Key(24, "Tree"), typeof(SaveTree), typeof(SaveSchema.EntityCodec)));
        types.AddRange(ArchiveResources.Metadata());
        return types.ToArray();
    }
    private static NativeArchiveType[] PrimaryTypes(bool raw)
        => [
            new NativeArchiveType<Position, SaveSchema.PositionCodec>(SaveSchema.Key(1, "Position"), default, raw),
            new NativeArchiveType<Velocity, SaveSchema.VelocityCodec>(SaveSchema.Key(2, "Velocity"), default, raw),
            new NativeArchiveType<Health, SaveSchema.HealthCodec>(SaveSchema.Key(3, "Health"), default, raw),
            new NativeArchiveType<Team, EmptyArchiveCodec<Team>>(SaveSchema.Key(4, "Team"), default, raw),
            new NativeArchiveType<Sleeping, EmptyArchiveCodec<Sleeping>>(SaveSchema.Key(5, "Sleeping"), default, raw),
            new NativeArchiveType<SaveCounter, SaveSchema.CounterCodec>(SaveSchema.Key(6, "Counter"), default, raw),
            new NativeArchiveType<SaveStep, SaveSchema.StepCodec>(SaveSchema.Key(7, "Step"), default, raw),
            new NativeArchiveType<IndexedNumber, SaveSchema.IndexCodec>(SaveSchema.Key(8, "Index"), default, raw),
            new NativeArchiveType<SharedNumberId, SaveSchema.SharedIdCodec>(SaveSchema.Key(9, "Shared"), default),
            new NativeArchiveType<SaveNativeFlags, SaveSchema.FlagsCodec>(SaveSchema.Key(10, "Enabled"), default),
            new NativeArchiveType<SaveNativeVersions, SaveSchema.VersionsCodec>(SaveSchema.Key(11, "Versions"), default),
            new NativeArchiveType<SaveNativeRemoved, SaveSchema.RemovedCodec>(SaveSchema.Key(12, "Removed"), default),
            new NativeArchiveType<SaveNativeBuffer, SaveSchema.BufferCodec>(SaveSchema.Key(13, "Buffer"), default, false,
                SaveSchema.Clone),
            new NativeArchiveType<SaveNativeOldBuffer, SaveSchema.OldBufferCodec>(SaveSchema.Key(14, "OldBuffer"), default, false,
                v => new() { Items = (SaveLink[])v.Items.Clone(), Version = v.Version }),
            new NativeArchiveType<ReferenceParent<Entity>, SaveSchema.ParentCodec>(SaveSchema.Key(15, "Parent"), default),
            new NativeArchiveType<ReferenceOldParent<Entity>, SaveSchema.OldParentCodec>(SaveSchema.Key(16, "OldParent"), default),
            new NativeArchiveType<ReferenceChildren<Entity>, SaveSchema.ChildrenCodec>(SaveSchema.Key(17, "Children"), default, false,
                SaveSchema.Clone, v => ArrayPool<Entity>.Shared.Return(v.Items!)),
            new NativeArchiveType<ReferenceOldChildren<Entity>, SaveSchema.OldChildrenCodec>(SaveSchema.Key(18, "OldChildren"), default, false,
                SaveSchema.Clone, v => ArrayPool<Entity>.Shared.Return(v.Items!)),
            new NativeArchiveType<SaveEdge, SaveSchema.EdgeCodec>(SaveSchema.Key(19, "Edge"), default, raw),
            new NativeArchiveType<ReferenceRelationEndpoints<Entity>, SaveSchema.EndpointsCodec>(SaveSchema.Key(20, "Endpoints"), default),
            new NativeArchiveType<ReferenceOldRelationEndpoints<Entity>, SaveSchema.OldEndpointsCodec>(SaveSchema.Key(21, "OldEndpoints"), default)
        ];
    internal override void Mutate()
    {
        Entity entity = Entities[1]; _values.Set(entity, new SaveCounter(999, 1));
        var versions = _values.Read<SaveNativeVersions>(entity); _values.Set(entity, versions with { Written = ++_clock.Value });
        _values.Set(entity, new SaveNativeFlags(true));
        var buffer = _values.Read<SaveNativeBuffer>(entity); buffer.Items[0] = new(entity, 999); buffer.Written = ++_clock.Value; _values.Set(entity, buffer);
        var sparseBefore = _sparse.Read<SaveSparseRow>(Entities[0]);
        _sparse.Set(Entities[0], new SaveSparseRow(new(entity, 999), sparseBefore.Added, ++_clock.Value));
        _resources.Read<SaveResource>().Items[0] = 999;
        Systems.Values[0] = 999; Complete(); _values.Create(); ++_clock.Value;
        _destroyedPosition = Store.DestroyedSequence;
    }
    public override void Complete() { _hierarchy.Maintain(); _relations.Maintain(); }
    internal override IDisposable Capture(bool preserve) => Serializer.Capture(Store, this, preserve);
    internal override void Restore(IDisposable snapshot) => ((CapturedNativeArchive)snapshot).Restore(Store, this);
    internal override void Save(IBufferWriter<byte> output, bool preserve) => Serializer.Save(Store, this, output, preserve);
    internal override void Save(Stream output, bool preserve) => Serializer.Save(Store, this, output, preserve);
    internal override void Load(ReadOnlySpan<byte> input) => Serializer.Load(Store, this, input);
    internal override void Load(Stream input) => Serializer.Load(Store, this, input);
    internal override Task SaveAsync(Stream output, bool preserve) => Serializer.SaveAsync(Store, this, output, preserve);
    internal override Task SaveFileAsync(string path, bool preserve) => Serializer.SaveFileAsync(Store, this, path, preserve);
    internal override void LoadFile(string path) { using var input = AtomicSaveFile.OpenRead(path); Load(input); }
    public void Write(ref DataWriter writer, bool preserve)
    {
        _hierarchy.WriteProgress(ref writer, new SaveSchema.EntityCodec());
        _relations.WriteProgress(ref writer, new SaveSchema.EntityCodec(), new SaveSchema.EntityCodec());
        writer.WriteInt32(_shared.Count);
        foreach (SaveShared value in _shared.Values.AsSpan(0, _shared.Count)) new SaveSchema.SharedCodec().Write(ref writer, value);
        using (var sparse = _sparse.OpenRead<SaveSparseRow>())
        {
            writer.WriteInt32(sparse.Entities.Length);
            for (int i = 0; i < sparse.Entities.Length; i++)
            {
                writer.WriteEntity(sparse.Entities[i]); new SaveSchema.LinkCodec().Write(ref writer, sparse.Values[i].Value);
                writer.WriteUInt64(sparse.Values[i].Added); writer.WriteUInt64(sparse.Values[i].Written);
            }
        }
        new SaveSchema.ResourceCodec().Write(ref writer, _resources.Read<SaveResource>());
        ArchiveResources.Write(new NativeArchiveResources(_resources, _resourceCounter), ref writer);
        writer.WriteInt32(Systems.Values.Length);
        foreach (ulong value in Systems.Values) writer.WriteUInt64(preserve ? value : Version);
        WriteDestroyed(ref writer, preserve ? Store.Destroyed : [], Store.DestroyedSequence, preserve ? _destroyedPosition : Store.DestroyedSequence);
    }
    public void Read(ref DataReader reader, bool preserve)
    {
        _hierarchy.ReadProgress(ref reader, new SaveSchema.EntityCodec());
        _relations.ReadProgress(ref reader, new SaveSchema.EntityCodec(), new SaveSchema.EntityCodec());
        _shared = new(); int count = reader.ReadInt32();
        for (int i = 0; i < count; i++) { new SaveSchema.SharedCodec().Read(ref reader, out SaveShared value); _shared.Intern(value); }
        count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            Entity entity = reader.ReadEntity(); new SaveSchema.LinkCodec().Read(ref reader, out SaveLink value);
            _sparse.Set(entity, new SaveSparseRow(value, reader.ReadUInt64(), reader.ReadUInt64()));
        }
        new SaveSchema.ResourceCodec().Read(ref reader, out SaveResource resource); _resources.Set(resource);
        ArchiveResources.Read(new NativeArchiveResources(_resources, _resourceCounter), ref reader);
        count = reader.ReadInt32();
        for (int i = 0; i < count; i++) { ulong value = reader.ReadUInt64(); Systems.Values[i] = preserve ? value : Version; }
        Systems.RestoreVersions(Systems.Values);
        Store.DestroyedSequence = reader.ReadUInt64(); _destroyedPosition = reader.ReadUInt64();
        Store.Destroyed.Clear(); count = reader.ReadInt32();
        for (int i = 0; i < count; i++) Store.Destroyed.Add(new(reader.ReadEntity(), reader.ReadUInt64()));
        RebuildIndexes();
        if (!preserve) RebaseChanges();
    }
    private void RebuildIndexes()
    {
        if (Store.HasNativeIndexes) return;
        _index = new(Store.Used + 1, e => e.Id); _groups = new(Store.Used + 1, e => e.Id);
        for (int id = 1; id <= Store.Used; id++) if (Store.AliveIds[id])
        {
            Entity entity = new(id, Store.Generations[id]);
            if (_values.Has<IndexedNumber>(entity)) _index.Set(entity, _values.Read<IndexedNumber>(entity).Value);
            if (_values.Has<SharedNumberId>(entity)) _groups.Set(entity, _shared.Values[_values.Read<SharedNumberId>(entity).Value].Value);
        }
    }
    public void ReleaseValues()
    {
        _hierarchy.Dispose(); _relations.Dispose(); _sparse.Dispose(); _resources.Remove<SaveResource>();
        ArchiveResources.Release(new NativeArchiveResources(_resources, _resourceCounter));
    }
    internal override void Check(bool preserved, bool maintained = false)
    {
        bool pending = preserved && !maintained && GrowthEntities.Count == 0;
        bool growthPending = preserved && !maintained;
        int count = Entities.Length;
        int growth = GrowthEntities.Count;
        if (Store.Count != (count + growth) * 2 + 2 || Store.IsAlive(Dead)) throw new InvalidOperationException("Native archive identity count differs.");
        for (int i = 0; i < count; i++)
        {
            Entity entity = Entities[i], next = Entities[(i + 1) % count];
            bool valid = Store.IsAlive(entity) && _values.Read<Position>(entity).X == i && _values.Read<Velocity>(entity).Y == 2
                && _values.Read<Health>(entity).Value == 100 && _values.Has<Team>(entity) == (i % 2 == 0)
                && _values.Has<Sleeping>(entity) == (i % 4 == 0) && _values.Read<SaveNativeFlags>(entity).Disabled == (i % 4 == 0)
                && _values.Read<SaveStep>(entity).Value == 2 && _values.Read<IndexedNumber>(entity) == new IndexedNumber(i % 16, i)
                && _shared.Values[_values.Read<SharedNumberId>(entity).Value] == new SaveShared(i % 16);
            if (i % 100 == 0)
                valid &= !_values.Has<SaveCounter>(entity) && _values.Read<SaveNativeRemoved>(entity).Value == new SaveCounter(i + 2, i)
                    && _values.Read<SaveNativeOldBuffer>(entity).Items.AsSpan().SequenceEqual(NativeFullArchiveWorld.Links(entity, next, i));
            else valid &= _values.Read<SaveCounter>(entity) == new SaveCounter(i + 2, i)
                && _values.Read<SaveNativeBuffer>(entity).Items.AsSpan().SequenceEqual(NativeFullArchiveWorld.Links(entity, next, i));
            if (i % 4 == 0) valid &= _sparse.Read<SaveSparseRow>(entity).Value == new SaveLink(next, i * 7L);
            var endpoints = _values.Read<ReferenceRelationEndpoints<Entity>>(Edges[i]);
            valid &= endpoints.Source == (i % 4 == 0 ? SecondRoot : FirstRoot) && endpoints.Target == entity
                && _values.Read<SaveEdge>(Edges[i]) == SaveEdge.Create(i)
                && _hierarchy.Parent(entity) == (i % 4 == 0 ? SecondRoot : FirstRoot);
            if (!valid) throw new InvalidOperationException("Native archive main values differ at " + i);
        }
        var first = _values.Read<ReferenceChildren<Entity>>(FirstRoot);
        if (first.Items![0] != Entities[^1] || first.Count != (pending ? count : count * 3 / 4) + (growthPending ? growth : growth * 3 / 4)
            || _hierarchy.ChildCount(SecondRoot) != (pending ? 0 : count / 4) + (growthPending ? 0 : growth / 4)) throw new InvalidOperationException("Native archive applied hierarchy differs.");
        var outgoing = _relations.ArchiveHead(FirstRoot, false); var future = _relations.ArchiveHead(SecondRoot, false);
        if (outgoing.Count != (pending ? count : count * 3 / 4) + (growthPending ? growth : growth * 3 / 4) || outgoing.First != Edges[^1]
            || future.Count != (pending ? 0 : count / 4) + (growthPending ? 0 : growth / 4) || !outgoing.Ordered || !future.Ordered
            || !_hierarchy.ArchiveIsOrdered(FirstRoot) || !_hierarchy.ArchiveIsOrdered(SecondRoot))
            throw new InvalidOperationException("Native archive adjacency or order policies differ.");
        int indexed = Store.HasNativeIndexes ? Store.IndexCount(7, false) : _index.Find(7).Length;
        int grouped = Store.HasNativeIndexes ? Store.IndexCount(7, true) : _groups.Find(7).Length;
        if (indexed != count / 16 + (growth + 8) / 16 || grouped != count / 16 + (growth + 8) / 16)
            throw new InvalidOperationException("Native archive derived indexes differ.");
        if (_resources.Read<SaveResource>().Items[0] != 0 || _resources.Read<SaveResource>().Items[63] != 819)
            throw new InvalidOperationException("Native archive resource differs.");
        if (preserved && !Systems.Values.AsSpan().SequenceEqual([7UL, 9UL, 11UL, 13UL])) throw new InvalidOperationException("Native archive system progress differs.");
        if (!preserved && Systems.Values.Any(v => v != Version)) throw new InvalidOperationException("Native archive rebuilt system baseline differs.");
        ArchiveResources.Check(ArchiveResources.Capture(new NativeArchiveResources(_resources, _resourceCounter)));
        int destroyed = 0;
        foreach (DestroyedEntity fact in Store.Destroyed) if (fact.Sequence > _destroyedPosition)
        { destroyed++; if (fact.Entity != Dead) throw new InvalidOperationException("Native archive destruction identity differs."); }
        if (destroyed != (preserved ? 1 : 0)) throw new InvalidOperationException("Native archive destruction progress differs.");
        for (int i = 0; i < growth; i++)
        {
            Entity entity = GrowthEntities[i]; var endpoints = _values.Read<ReferenceRelationEndpoints<Entity>>(GrowthEdges[i]);
            bool valid = Store.IsAlive(entity) && _values.Read<Position>(entity).X == i && _values.Read<Velocity>(entity).Y == 2
                && _values.Read<Health>(entity).Value == 100 && _values.Read<IndexedNumber>(entity).Value == i % 16
                && _shared.Values[_values.Read<SharedNumberId>(entity).Value].Value == i % 16 && _values.Read<SaveNativeFlags>(entity).Disabled == (i % 4 == 0)
                && _hierarchy.Parent(entity) == (i % 4 == 0 ? SecondRoot : FirstRoot)
                && endpoints.Source == (i % 4 == 0 ? SecondRoot : FirstRoot) && endpoints.Target == entity
                && _values.Read<SaveEdge>(GrowthEdges[i]) == SaveEdge.Create(i);
            if (i % 100 == 0)
                valid &= !_values.Has<SaveCounter>(entity) && _values.Read<SaveNativeRemoved>(entity).Value == new SaveCounter(i + 2, i)
                    && _values.Read<SaveNativeOldBuffer>(entity).Items.AsSpan().SequenceEqual(NativeFullArchiveWorld.Links(entity, Entities[i % count], i));
            else valid &= _values.Read<SaveCounter>(entity) == new SaveCounter(i + 2, i)
                && _values.Read<SaveNativeBuffer>(entity).Items.AsSpan().SequenceEqual(NativeFullArchiveWorld.Links(entity, Entities[i % count], i));
            if (i % 4 == 0) valid &= _sparse.Read<SaveSparseRow>(entity).Value == new SaveLink(Entities[i % count], i * 7L);
            if (!valid) throw new InvalidOperationException("Native archive growth values differ at " + i);
        }
    }
    internal override void Clear()
    {
        _lifecycleJobs?.Complete();
        ReleaseValues(); _hierarchy.ClearProgress(); Store.ClearWorld(); _clock.Value++;
        _shared = new(); _destroyedPosition = Store.DestroyedSequence;
        if (!Store.HasNativeIndexes) { _index = new(16, e => e.Id); _groups = new(16, e => e.Id); }
    }
    internal override void CheckCleared()
    {
        int indexed = Store.HasNativeIndexes ? Store.IndexCount(7, false) : _index.Find(7).Length;
        int grouped = Store.HasNativeIndexes ? Store.IndexCount(7, true) : _groups.Find(7).Length;
        if (Store.Count != 0 || Store.IsAlive(Entities[0]) || Store.IsAlive(FirstRoot) || indexed != 0 || grouped != 0
            || _sparse.Store<SaveSparseRow>().Count != 0 || Store.Destroyed.Count != 0 || _destroyedPosition != Store.DestroyedSequence
            || _hierarchy.ArchiveIsOrdered(FirstRoot))
            throw new InvalidOperationException("Native mixed lifecycle clear differs.");
    }
    internal override void CheckDisposed()
    {
        if (Store.Count != 0 || Store.Used != 0 || !_destroyedCompleted)
            throw new InvalidOperationException("Native mixed lifecycle final completion differs.");
    }
    internal override MixedLifecycleJobs ConnectJobs(Scheduler scheduler)
        => _lifecycleJobs = Store.ConnectLifecycleJobs(scheduler, () => Version, Entities.Length);
    internal override TrackedArchiveJobs ConnectTrackedJobs(Scheduler scheduler)
        => Store.ConnectTrackedJobs(scheduler, new(this, Store, scheduler, _sparse, _resources, Entities, SecondRoot, () => ++_clock.Value));
    internal override void Grow()
    {
        Complete();
        var edits = new HierarchyEdit<Entity>[1000];
        var retargets = new RelationEdit<Entity>[250]; var deferred = new HierarchyEdit<Entity>[250];
        for (int i = 0; i < edits.Length; i++)
        {
            Entity entity = _values.Create(InitialColumns);
            _values.Set(entity, new Position { X = i }); _values.Set(entity, new Velocity { X = 1, Y = 2 });
            _values.Set(entity, new Health { Value = 100, Max = 100 });
            _values.Set(entity, new SaveCounter(i + 2, i)); _values.Set(entity, new SaveStep(2));
            _values.Set(entity, new SaveNativeFlags(i % 4 == 0)); _values.Set(entity, new SaveNativeVersions(++_clock.Value, Version));
            _values.Set(entity, new IndexedNumber(i % 16, i)); _values.Set(entity, new SharedNumberId(_shared.Intern(new(i % 16))));
            if (!Store.HasNativeIndexes) { _index.Set(entity, i % 16); _groups.Set(entity, i % 16); }
            _values.Set(entity, new SaveNativeBuffer { Items = NativeFullArchiveWorld.Links(entity, Entities[i % Entities.Length], i), Added = ++_clock.Value, Written = ++_clock.Value });
            if (i % 4 == 0) _sparse.Set(entity, new SaveSparseRow(new(Entities[i % Entities.Length], i * 7L), ++_clock.Value, Version));
            if (i % 100 == 0)
            {
                _values.Set(entity, new SaveNativeRemoved(_values.Read<SaveCounter>(entity), ++_clock.Value)); _values.Remove<SaveCounter>(entity);
                _values.Set(entity, new SaveNativeOldBuffer { Items = _values.Read<SaveNativeBuffer>(entity).Items, Version = ++_clock.Value }); _values.Remove<SaveNativeBuffer>(entity);
            }
            GrowthEntities.Add(entity); edits[i] = new(entity, FirstRoot);
            Entity edge = _relations.CreateEdge(FirstRoot, entity, i); GrowthEdges.Add(edge);
            if (i % 4 == 0) { deferred[i / 4] = new(entity, SecondRoot); retargets[i / 4] = new(edge, SecondRoot, entity); }
        }
        _hierarchy.SetParents(edits, false);
        _hierarchy.SetParents(deferred, true); _relations.Retarget(retargets, true);
    }
    public override void Dispose()
    { _lifecycleJobs?.Complete(); ReleaseValues(); Store.Dispose(); Store.CloseIdentities(); _lifecycleJobs?.Dispose(); _destroyedCompleted = true; }
}

internal readonly struct EmptyArchiveCodec<T> : IComponentCodec<T> where T : struct
{
    public void Write(ref DataWriter writer, in T value) { }
    public void Read(ref DataReader reader, out T value) => value = default;
}
