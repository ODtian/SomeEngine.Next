using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal abstract class FullArchiveWorld : IDisposable
{
    internal readonly List<Entity> GrowthEntities = new(), GrowthEdges = new();
    public abstract ulong Version { get; }
    internal abstract Position ReadPosition(int ordinal);
    internal abstract long ResourceReleases { get; }
    internal abstract bool InitialResourceReleased { get; }
    internal abstract void Clear();
    internal abstract void CheckCleared();
    internal abstract void CheckDisposed();
    internal abstract MixedLifecycleJobs ConnectJobs(Scheduler scheduler);
    internal abstract TrackedArchiveJobs ConnectTrackedJobs(Scheduler scheduler);
    internal abstract ArchiveWorldGroup ConnectArchiveGroup(Scheduler scheduler);
    internal abstract void Grow();
    internal abstract IDisposable Capture(bool preserve);
    internal abstract void Restore(IDisposable snapshot);
    internal abstract void Save(IBufferWriter<byte> output, bool preserve);
    internal abstract void Save(Stream output, bool preserve);
    internal abstract void Load(ReadOnlySpan<byte> input);
    internal abstract void Load(Stream input);
    internal abstract Task SaveAsync(Stream output, bool preserve);
    internal abstract Task SaveFileAsync(string path, bool preserve);
    internal abstract void LoadFile(string path);
    internal abstract void Mutate();
    internal abstract void Check(bool preserved, bool maintained = false);
    public abstract void Complete();
    public abstract void Dispose();
}

internal sealed partial class SourceFullArchiveWorld : FullArchiveWorld
{
    internal readonly World World;
    internal readonly Entity[] Entities, Edges;
    internal readonly Entity FirstRoot, SecondRoot, Dead;
    internal readonly SaveSystemVersions Systems = new();
    internal readonly SaveResource InitialResource;
    private readonly ResourceCounter _resourceCounter = new();
    private readonly DestroyedReader _destroyed;
    private readonly WorldSerializer _serializer;
    private readonly SavePurpose _contract;
    public override ulong Version => World.Version;
    internal override Position ReadPosition(int ordinal) => World.ReadValue<Position>(Entities[ordinal]);
    internal override long ResourceReleases => _resourceCounter.Count;
    internal override bool InitialResourceReleased => InitialResource.Released;

    internal SourceFullArchiveWorld(int count, bool raw, Scheduler? scheduler = null)
    {
        int capacity = count * 2 + 16;
        World = new(capacity, execution: scheduler is null ? null : new WorldJobs(scheduler)); Entities = new Entity[count]; Edges = new Entity[count];
        using var structure = World.OpenStructureWrite();
        _destroyed = World.WatchDestroyed();
        World.GroupBy<SaveShared>(); World.Index<IndexedNumber, int>();
        for (int i = 0; i < count; i++)
        {
            var archive = new ArchiveBody
            {
                Body = Body.Moving(new Position { X = i }), Counter = new(i + 2, i), Step = new(2),
                Number = new(i % 16, i), Shared = new(i % 16),
            };
            Entity entity = Entities[i] = i % 4 == 0 ? World.Spawn(new ArchiveSleepingTeamBody { Archive = archive })
                : i % 2 == 0 ? World.Spawn(new ArchiveTeamBody { Archive = archive }) : World.Spawn(archive);
            if (i % 4 == 0) World.Disable<SaveStep>(entity);
        }
        Span<SaveLink> links = stackalloc SaveLink[4];
        for (int i = 0; i < count; i++)
        {
            Entity entity = Entities[i], next = Entities[(i + 1) % count];
            NativeFullArchiveWorld.WriteLinks(links, entity, next, i);
            using (var buffer = World.GetBuffer<SaveLink>(entity)) buffer.AddRange(links);
            if (i % 4 == 0) World.Set(entity, new SaveLink(next, i * 7L));
            if (i % 100 == 0) { World.Remove<SaveCounter>(entity); World.RemoveBuffer<SaveLink>(entity); }
        }
        FirstRoot = World.Create(); SecondRoot = World.Create();
        var parents = new ParentEdit[count]; var order = new Entity[count]; order[0] = Entities[^1];
        for (int i = 0; i < count; i++) { parents[i] = new(Entities[i], FirstRoot); if (i + 1 < count) order[i + 1] = Entities[i]; }
        Hierarchy<SaveTree>.SetParents(World, parents); Hierarchy<SaveTree>.SetOrdered(World, FirstRoot, true, order);
        Hierarchy<SaveTree>.SetOrdered(World, SecondRoot, true);
        for (int i = 0; i < count; i++) Edges[i] = World.CreateRelation(FirstRoot, Entities[i], SaveEdge.Create(i)).Entity;
        Relations<SaveEdge>.SetOrdered(World, FirstRoot, RelationAdjacencyRole.Outgoing, true);
        Relations<SaveEdge>.SetOrdered(World, SecondRoot, RelationAdjacencyRole.Outgoing, true);
        Relations<SaveEdge>.SetOrdered(World, FirstRoot, RelationAdjacencyRole.Incoming, true);
        Relations<SaveEdge>.SetOrdered(World, SecondRoot, RelationAdjacencyRole.Incoming, true);
        Relations<SaveEdge>.Reorder(World, FirstRoot, RelationAdjacencyRole.Outgoing, new(Edges[^1]), 0);
        var deferred = new ParentEdit[count / 4]; var retargets = new RelationRetarget<SaveEdge>[count / 4];
        for (int i = 0; i < deferred.Length; i++)
        { deferred[i] = new(Entities[i * 4], SecondRoot); retargets[i] = new(new(Edges[i * 4]), SecondRoot, Entities[i * 4]); }
        Hierarchy<SaveTree>.SetParents(World, deferred, true); World.RetargetRelations(retargets, true);
        Dead = World.Create(); World.Destroy(Dead);
        InitialResource = new(); for (int i = 0; i < InitialResource.Items.Length; i++) InitialResource.Items[i] = i * 13L;
        World.SetResource(InitialResource);
        ArchiveResources.Initialize(new SourceArchiveResources(World, _resourceCounter));
        _contract = raw ? SavePurpose.RawCheckpoint : SavePurpose.DurableSave;
        _serializer = new(CreateRegistry(raw));
    }
    private SerializationRegistry CreateRegistry(bool raw)
    {
        var registry = new SerializationRegistry();
        if (raw) registry.Register<Team>(SaveSchema.Key(4, "Team")).Register<Sleeping>(SaveSchema.Key(5, "Sleeping"));
        else registry.Register<Team, EmptyArchiveCodec<Team>>(SaveSchema.Key(4, "Team"))
            .Register<Sleeping, EmptyArchiveCodec<Sleeping>>(SaveSchema.Key(5, "Sleeping"));
        if (raw)
            registry.Register<Position>(SaveSchema.Key(1, "Position")).Register<Velocity>(SaveSchema.Key(2, "Velocity"))
                .Register<Health>(SaveSchema.Key(3, "Health")).Register<SaveCounter>(SaveSchema.Key(6, "Counter"))
                .Register<SaveStep>(SaveSchema.Key(7, "Step")).Register<IndexedNumber>(SaveSchema.Key(8, "Index"))
                .RegisterShared<SaveShared>(SaveSchema.Key(9, "Shared")).RegisterBuffer<SaveLink>(SaveSchema.Key(13, "Buffer"))
                .RegisterSparse<SaveLink>(SaveSchema.Key(22, "Sparse"));
        else
            registry.Register<Position, SaveSchema.PositionCodec>(SaveSchema.Key(1, "Position"))
                .Register<Velocity, SaveSchema.VelocityCodec>(SaveSchema.Key(2, "Velocity"))
                .Register<Health, SaveSchema.HealthCodec>(SaveSchema.Key(3, "Health"))
                .Register<SaveCounter, SaveSchema.CounterCodec>(SaveSchema.Key(6, "Counter"))
                .Register<SaveStep, SaveSchema.StepCodec>(SaveSchema.Key(7, "Step"))
                .Register<IndexedNumber, SaveSchema.IndexCodec>(SaveSchema.Key(8, "Index"))
                .RegisterShared<SaveShared, SaveSchema.SharedCodec>(SaveSchema.Key(9, "Shared"))
                .RegisterBuffer<SaveLink, SaveSchema.LinkCodec>(SaveSchema.Key(13, "Buffer"))
                .RegisterSparse<SaveLink, SaveSchema.LinkCodec>(SaveSchema.Key(22, "Sparse"));
        ArchiveResources.Register(registry, _resourceCounter);
        return registry.RegisterResource<SaveResource, SaveSchema.ResourceCodec>(SaveSchema.Key(23, "Resource"))
            .RegisterHierarchy<SaveTree>(SaveSchema.Key(24, "Tree"))
            .RegisterRelation<SaveEdge, SaveSchema.EdgeCodec>(SaveSchema.Key(19, "Edge"));
    }
    internal override IDisposable Capture(bool preserve) => new WorldCheckpoint().Capture(World, Progress(preserve), Systems);
    internal override void Restore(IDisposable snapshot) => ((WorldCheckpoint)snapshot).Restore(World, Systems);
    private static CheckpointProgress Progress(bool preserve) => preserve ? CheckpointProgress.Preserve : CheckpointProgress.Rebuild;
    internal override void Save(IBufferWriter<byte> output, bool preserve) => _serializer.Save(World, output, _contract, Progress(preserve), Systems);
    internal override void Save(Stream output, bool preserve) => _serializer.Save(World, output, _contract, Progress(preserve), Systems);
    internal override void Load(ReadOnlySpan<byte> input) => _serializer.Load(World, input, null, Systems);
    internal override void Load(Stream input) => _serializer.Load(World, input, null, Systems);
    internal override Task SaveAsync(Stream output, bool preserve) => _serializer.SaveAsync(World, output, _contract, Progress(preserve), Systems);
    internal override Task SaveFileAsync(string path, bool preserve) => _serializer.SaveFileAsync(World, path, _contract, Progress(preserve), Systems);
    internal override void LoadFile(string path) => _serializer.LoadFile(World, path, null, Systems);
    internal override void Mutate()
    {
        Entity entity = Entities[1]; World.Set(entity, new SaveCounter(999, 1)); World.Disable<SaveStep>(entity);
        using (var buffer = World.GetBuffer<SaveLink>(entity)) buffer[0] = new(entity, 999);
        World.Set(Entities[0], new SaveLink(entity, 999));
        using (var resource = World.OpenResourceWrite<SaveResource>()) resource.Value.Items[0] = 999;
        Systems.Values[0] = 999; Complete(); World.Create();
        ulong through; using (var facts = _destroyed.Open()) through = facts.Through; _destroyed.Advance(through);
    }
    public override void Complete() { Hierarchy<SaveTree>.Maintain(World); World.MaintainRelations<SaveEdge>(); }
    internal override void Clear() => World.Clear();
    internal override MixedLifecycleJobs ConnectJobs(Scheduler scheduler) => new SourceMixedLifecycleJobs(World, scheduler, Entities.Length);
    internal override void Grow()
    {
        using var structure = World.OpenStructureWrite();
        Complete();
        var edits = new ParentEdit[1000];
        var retargets = new RelationRetarget<SaveEdge>[250];
        var deferred = new ParentEdit[250];
        Span<SaveLink> links = stackalloc SaveLink[4];
        for (int i = 0; i < edits.Length; i++)
        {
            Entity entity = World.Spawn(new ArchiveBody
            {
                Body = Body.Moving(new Position { X = i }), Counter = new(i + 2, i), Step = new(2),
                Number = new(i % 16, i), Shared = new(i % 16),
            });
            if (i % 4 == 0) World.Disable<SaveStep>(entity);
            NativeFullArchiveWorld.WriteLinks(links, entity, Entities[i % Entities.Length], i);
            using (var buffer = World.GetBuffer<SaveLink>(entity)) buffer.AddRange(links);
            if (i % 4 == 0) World.Set(entity, new SaveLink(Entities[i % Entities.Length], i * 7L));
            if (i % 100 == 0) { World.Remove<SaveCounter>(entity); World.RemoveBuffer<SaveLink>(entity); }
            GrowthEntities.Add(entity);
            edits[i] = new(entity, FirstRoot);
            Entity edge = World.CreateRelation(FirstRoot, entity, SaveEdge.Create(i)).Entity;
            GrowthEdges.Add(edge);
            if (i % 4 == 0) { deferred[i / 4] = new(entity, SecondRoot); retargets[i / 4] = new(new(edge), SecondRoot, entity); }
        }
        Hierarchy<SaveTree>.SetParents(World, edits);
        Hierarchy<SaveTree>.SetParents(World, deferred, true); World.RetargetRelations(retargets, true);
    }
    internal override void CheckCleared()
    {
        using var facts = _destroyed.Open();
        using var index = World.Index<IndexedNumber, int>().Find(7);
        if (World.EntityCount != 0 || World.IsAlive(Entities[0]) || World.IsAlive(FirstRoot) || facts.Count != 0
            || _destroyed.Position != facts.Through || index.Count != 0 || World.Query(new QueryDescription().InGroup<SaveShared>(7)).Count != 0)
            throw new InvalidOperationException("Source mixed lifecycle clear differs.");
    }
    internal override void CheckDisposed()
    {
        if (World.EntityCount != 0 || !_destroyed.IsCompleted)
            throw new InvalidOperationException("Source mixed lifecycle final completion differs.");
    }
    internal override void Check(bool preserved, bool maintained = false)
    {
        bool pending = preserved && !maintained && GrowthEntities.Count == 0;
        bool growthPending = preserved && !maintained;
        int count = Entities.Length;
        int growth = GrowthEntities.Count;
        if (World.EntityCount != (count + growth) * 2 + 2 || World.IsAlive(Dead)) throw new InvalidOperationException("Source archive identity count differs.");
        for (int i = 0; i < count; i++)
        {
            Entity entity = Entities[i], next = Entities[(i + 1) % count];
            bool valid = World.IsAlive(entity) && World.ReadValue<Position>(entity).X == i && World.ReadValue<Velocity>(entity).Y == 2
                && World.ReadValue<Health>(entity).Value == 100 && World.Has<Team>(entity) == (i % 2 == 0)
                && World.Has<Sleeping>(entity) == (i % 4 == 0) && World.IsEnabled<SaveStep>(entity) == (i % 4 != 0)
                && World.ReadValue<SaveStep>(entity).Value == 2 && World.ReadValue<IndexedNumber>(entity) == new IndexedNumber(i % 16, i)
                && World.ReadValue<SaveShared>(entity) == new SaveShared(i % 16);
            if (i % 100 == 0)
            {
                valid &= !World.Has<SaveCounter>(entity) && World.ReadValue<Removed<SaveCounter>>(entity).Value == new SaveCounter(i + 2, i);
                using var buffer = World.ReadRemovedBuffer<SaveLink>(entity);
                valid &= buffer.AsSpan().SequenceEqual(NativeFullArchiveWorld.Links(entity, next, i));
            }
            else
            {
                valid &= World.ReadValue<SaveCounter>(entity) == new SaveCounter(i + 2, i);
                using var buffer = World.ReadBuffer<SaveLink>(entity);
                valid &= buffer.AsSpan().SequenceEqual(NativeFullArchiveWorld.Links(entity, next, i));
            }
            if (i % 4 == 0) using (var sparse = World.OpenRead<SaveLink>()) valid &= sparse.Read(entity) == new SaveLink(next, i * 7L);
            var endpoints = World.ReadValue<RelationEndpoints<SaveEdge>>(Edges[i]);
            valid &= endpoints.Source == (i % 4 == 0 ? SecondRoot : FirstRoot) && endpoints.Target == entity
                && World.ReadValue<SaveEdge>(Edges[i]) == SaveEdge.Create(i)
                && Hierarchy<SaveTree>.GetParent(World, entity) == (i % 4 == 0 ? SecondRoot : FirstRoot);
            if (!valid) throw new InvalidOperationException("Source archive main values differ at " + i);
        }
        using (var first = Hierarchy<SaveTree>.GetChildren(World, FirstRoot))
            if (first.Entities[0] != Entities[^1] || first.Count != (pending ? count : count * 3 / 4) + (growthPending ? growth : growth * 3 / 4))
                throw new InvalidOperationException("Source archive applied hierarchy differs.");
        using (var second = Hierarchy<SaveTree>.GetChildren(World, SecondRoot))
            if (second.Count != (pending ? 0 : count / 4) + (growthPending ? 0 : growth / 4)) throw new InvalidOperationException("Source archive future hierarchy differs.");
        if (!Hierarchy<SaveTree>.IsOrdered(World, FirstRoot) || !Hierarchy<SaveTree>.IsOrdered(World, SecondRoot)
            || !Relations<SaveEdge>.IsOrdered(World, FirstRoot, RelationAdjacencyRole.Outgoing)
            || !Relations<SaveEdge>.IsOrdered(World, SecondRoot, RelationAdjacencyRole.Outgoing))
            throw new InvalidOperationException("Source archive local order policies differ.");
        using (var edges = World.GetOutgoingRelations<SaveEdge>(FirstRoot))
            if (edges.Count != (pending ? count : count * 3 / 4) + (growthPending ? growth : growth * 3 / 4) || edges.Entries[0].Edge != Edges[^1])
                throw new InvalidOperationException("Source archive applied adjacency differs.");
        using (var found = World.Index<IndexedNumber, int>().Find(7))
            if (found.Count != count / 16 + (growth + 8) / 16 || World.Query(new QueryDescription().InGroup<SaveShared>(7)).Count != count / 16 + (growth + 8) / 16)
                throw new InvalidOperationException("Source archive derived indexes differ.");
        using (var resource = World.OpenResourceRead<SaveResource>())
            if (resource.Value.Items[0] != 0 || resource.Value.Items[63] != 819) throw new InvalidOperationException("Source archive resource differs.");
        if (preserved && !Systems.Values.AsSpan().SequenceEqual([7UL, 9UL, 11UL, 13UL])) throw new InvalidOperationException("Source archive system progress differs.");
        if (!preserved && Systems.Values.Any(v => v != Version)) throw new InvalidOperationException("Source archive rebuilt system baseline differs.");
        ArchiveResources.Check(ArchiveResources.Capture(new SourceArchiveResources(World, _resourceCounter)));
        using (var facts = _destroyed.Open())
        {
            if (facts.Count != (preserved ? 1 : 0)) throw new InvalidOperationException("Source archive destruction progress differs.");
            foreach (DestroyedEntity fact in facts) if (fact.Entity != Dead) throw new InvalidOperationException("Source archive destruction identity differs.");
        }
        for (int i = 0; i < growth; i++)
        {
            Entity entity = GrowthEntities[i]; var endpoints = World.ReadValue<RelationEndpoints<SaveEdge>>(GrowthEdges[i]);
            bool valid = World.IsAlive(entity) && World.ReadValue<Position>(entity).X == i && World.ReadValue<Velocity>(entity).Y == 2
                && World.ReadValue<Health>(entity).Value == 100 && World.ReadValue<IndexedNumber>(entity).Value == i % 16
                && World.ReadValue<SaveShared>(entity).Value == i % 16 && World.IsEnabled<SaveStep>(entity) == (i % 4 != 0)
                && Hierarchy<SaveTree>.GetParent(World, entity) == (i % 4 == 0 ? SecondRoot : FirstRoot)
                && endpoints.Source == (i % 4 == 0 ? SecondRoot : FirstRoot) && endpoints.Target == entity
                && World.ReadValue<SaveEdge>(GrowthEdges[i]) == SaveEdge.Create(i);
            if (i % 100 == 0)
            {
                valid &= !World.Has<SaveCounter>(entity) && World.ReadValue<Removed<SaveCounter>>(entity).Value == new SaveCounter(i + 2, i);
                using var buffer = World.ReadRemovedBuffer<SaveLink>(entity);
                valid &= buffer.AsSpan().SequenceEqual(NativeFullArchiveWorld.Links(entity, Entities[i % count], i));
            }
            else
            {
                valid &= World.ReadValue<SaveCounter>(entity) == new SaveCounter(i + 2, i);
                using var buffer = World.ReadBuffer<SaveLink>(entity);
                valid &= buffer.AsSpan().SequenceEqual(NativeFullArchiveWorld.Links(entity, Entities[i % count], i));
            }
            if (i % 4 == 0) using (var sparse = World.OpenRead<SaveLink>()) valid &= sparse.Read(entity) == new SaveLink(Entities[i % count], i * 7L);
            if (!valid) throw new InvalidOperationException("Source archive growth values differ at " + i);
        }
    }
    public override void Dispose() => World.Dispose();
}
