using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed partial class RelationshipChunkJobTests : JobTest
{
    [Fact]
    public void ParentBatchWritesMainValuesAndKeepsAppliedChildrenUntilMaintenance()
    {
        World world = CreateJobWorld();
        Entity oldParent = world.Create(), newParent = world.Create();
        Entity first = world.Create(), second = world.Create();
        Hierarchy<Domain>.SetParent(world, first, oldParent);
        Hierarchy<Domain>.SetParent(world, second, oldParent);
        var ordering = new OrderingProbe();
        JobHandle dependency = Jobs.Schedule(new MarkDependency(ordering));
        JobHandle written = Jobs.Schedule(new SetParents(world, [new(first, newParent), new(second, newParent)], ordering),
            world.Execution!.StructureWrite, dependency);
        written.Complete();
        Assert.Equal(1, ordering.Completed);
        Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, first));
        Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, second));
        Assert.Equal([first, second], Children(world, oldParent));
        Assert.Empty(Children(world, newParent));
        Jobs.Schedule(new MaintainHierarchy(world), world.Execution.StructureWrite, written).Complete();
        Assert.Empty(Children(world, oldParent));
        Assert.Equal([first, second], Children(world, newParent));
    }

    [Fact]
    public void ParentBatchRejectsAnInvalidFinalForestBeforeChangingItsValues()
    {
        World world = CreateJobWorld();
        Entity root = world.Create(), first = world.Create(), second = world.Create();
        Hierarchy<Domain>.SetParent(world, first, root);
        Hierarchy<Domain>.SetParent(world, second, first);
        JobHandle written = Jobs.Schedule(new SetParents(world, [new(first, second)], null), world.Execution!.StructureWrite);
        Assert.Throws<ArgumentException>(written.Complete);
        Assert.Equal(root, Hierarchy<Domain>.GetParent(world, first));
        Assert.Equal(first, Hierarchy<Domain>.GetParent(world, second));
        Assert.Equal([first], Children(world, root));
        Assert.Equal([second], Children(world, first));
        Hierarchy<Domain>.Maintain(world);
        Assert.Equal(root, Hierarchy<Domain>.GetParent(world, first));
        Assert.Equal(first, Hierarchy<Domain>.GetParent(world, second));
    }

    [Fact]
    public void ParentColumnsRequireTypedHierarchyWritesForEveryQuerySelection()
    {
        World world = CreateJobWorld();
        Entity parent = world.Create(), child = world.Create(), marker = world.Create(new ChunkProbe());
        Hierarchy<Domain>.SetParent(world, child, parent);
        world.Add(child, default(EnabledProbe));
        world.Add(child, default(ChunkProbe));
        Query[] selections =
        [
            world.Query<Parent<Domain>>(),
            world.Query(new QueryDescription().Optional<Parent<Domain>>()),
            world.Query(new QueryDescription().WithStored<Parent<Domain>>().With<EnabledProbe>()),
            world.Query(new QueryDescription().WithStored<ChunkProbe>().Optional<Parent<Domain>>()),
        ];
        foreach (Query selection in selections)
            Assert.Throws<InvalidOperationException>(() =>
            {
                using WorldStructureWriteScope structure = world.OpenStructureWrite();
                using QueryScope rows = selection.Open();
                foreach (QueryChunk chunk in rows) _ = chunk.Write<Parent<Domain>>();
            });
        Assert.Equal(parent, Hierarchy<Domain>.GetParent(world, child));
        Assert.True(world.IsAlive(marker));
    }

    [Fact]
    public void DirectedEndpointBatchValidatesAUniqueTargetSwapTogether()
    {
        World world = CreateJobWorld();
        Entity sourceA = world.Create(), sourceB = world.Create(), targetA = world.Create(), targetB = world.Create();
        RelationEdge<UniqueTargetDirected> first = world.CreateRelation(sourceA, targetA, default(UniqueTargetDirected));
        RelationEdge<UniqueTargetDirected> second = world.CreateRelation(sourceB, targetB, default(UniqueTargetDirected));
        Jobs.Schedule(new SwapTargets(world, first, second, sourceA, sourceB, targetA, targetB), world.Execution!.StructureWrite).Complete();
        Assert.Equal(targetB, world.ReadComponentData<RelationEndpoints<UniqueTargetDirected>>(first.Entity).Target);
        Assert.Equal(targetA, world.ReadComponentData<RelationEndpoints<UniqueTargetDirected>>(second.Entity).Target);
        Assert.Equal([first.Entity], Incoming<UniqueTargetDirected>(world, targetA));
        Assert.Equal([second.Entity], Incoming<UniqueTargetDirected>(world, targetB));
        Jobs.Schedule(new MaintainRelations<UniqueTargetDirected>(world), world.Execution.StructureWrite).Complete();
        Assert.Equal([second.Entity], Incoming<UniqueTargetDirected>(world, targetA));
        Assert.Equal([first.Entity], Incoming<UniqueTargetDirected>(world, targetB));
    }

    [Fact]
    public void UndirectedEndpointBatchKeepsIncidentEntriesUntilMaintenance()
    {
        World world = CreateJobWorld();
        Entity first = world.Create(), oldSecond = world.Create(), newSecond = world.Create();
        RelationEdge<UndirectedPayload> edge = world.CreateRelation(first, oldSecond, default(UndirectedPayload));
        Jobs.Schedule(new RetargetUndirected(world, edge, first, newSecond), world.Execution!.StructureWrite).Complete();
        Assert.Equal(newSecond, world.ReadComponentData<RelationEndpoints<UndirectedPayload>>(edge.Entity).Target);
        Assert.Equal([edge.Entity], Incident<UndirectedPayload>(world, oldSecond));
        Assert.Empty(Incident<UndirectedPayload>(world, newSecond));
        Jobs.Schedule(new MaintainRelations<UndirectedPayload>(world), world.Execution.StructureWrite).Complete();
        Assert.Empty(Incident<UndirectedPayload>(world, oldSecond));
        Assert.Equal([edge.Entity], Incident<UndirectedPayload>(world, newSecond));
    }

    [Fact]
    public void ReadRangesExposeTheCurrentProtectedParentAndEndpointValues()
    {
        World world = CreateJobWorld();
        Entity parent = world.Create(), child = world.Create();
        Hierarchy<Domain>.SetParent(world, child, parent);
        Entity source = world.Create(), target = world.Create(), first = world.Create(), second = world.Create();
        RelationEdge<UniqueTargetDirected> directed = world.CreateRelation(source, target, default(UniqueTargetDirected));
        RelationEdge<UndirectedPayload> undirected = world.CreateRelation(first, second, default(UndirectedPayload));
        var parents = new List<(Entity, Entity)>();
        var directedValues = new List<(Entity, Entity, Entity)>();
        var undirectedValues = new List<(Entity, Entity, Entity)>();
        new CaptureParents(parents).Schedule(world).Complete();
        new CaptureDirected(directedValues).Schedule(world).Complete();
        new CaptureUndirected(undirectedValues).Schedule(world).Complete();
        Assert.Equal((child, parent), Assert.Single(parents));
        Assert.Equal((directed.Entity, source, target), Assert.Single(directedValues));
        Assert.Equal((undirected.Entity, first, second), Assert.Single(undirectedValues));
    }

    [Fact]
    public void ParentReadRangesFollowTheSameWorldStructureWriter()
    {
        World world = CreateJobWorld();
        Entity parent = world.Create(), child = world.Create();
        Hierarchy<Domain>.SetParent(world, child, parent);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var parents = new List<(Entity, Entity)>();
        JobHandle writer = Jobs.Schedule(new HoldStructure(entered, release), world.Execution!.StructureWrite);
        JobHandle reader = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            reader = new CaptureParents(parents).Schedule(world);
            release.Set();
            reader.Complete();
            writer.Complete();
        }
        finally { release.Set(); writer.Complete(); reader.Complete(); }
        Assert.Equal((child, parent), Assert.Single(parents));
    }

    [Fact]
    public void ParentReadRangesRequireAccessToTheirOwnWorld()
    {
        World world = CreateJobWorld(), other = CreateJobWorld();
        Entity parent = world.Create(), child = world.Create();
        Hierarchy<Domain>.SetParent(world, child, parent);
        Query query = world.Query<Parent<Domain>>();
        var capture = new List<Entity>();
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(new ReadParentIdentities(query, capture)).Complete());
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(new ReadParentIdentities(query, capture), other.Execution!.StructureRead).Complete());
        Assert.Empty(capture);
    }

    private static Entity[] Children(World world, Entity parent)
    {
        using ChildrenRead children = Hierarchy<Domain>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }
    private static Entity[] Incoming<T>(World world, Entity target) where T : struct
    {
        using RelationRead edges = Relations<T>.Incoming(world, target);
        Entity[] result = new Entity[edges.Count];
        for (int index = 0; index < result.Length; index++) result[index] = edges.Entries[index].Edge;
        return result;
    }
    private static Entity[] Incident<T>(World world, Entity target) where T : struct
    {
        using IncidentRelationsRead edges = Relations<T>.Incident(world, target);
        Entity[] result = new Entity[edges.Count];
        int index = 0;
        foreach (RelationEntry entry in edges) result[index++] = entry.Edge;
        return result;
    }
    private sealed class OrderingProbe { internal int Completed; }
    private readonly struct MarkDependency(OrderingProbe probe) : IJob
    {
        public void Execute() => probe.Completed = 1;
    }
    private readonly struct SetParents(World world, ParentEdit[] edits, OrderingProbe? probe) : IJob
    {
        public void Execute()
        {
            if (probe is not null) Assert.Equal(1, probe.Completed);
            Hierarchy<Domain>.SetParents(world, edits, deferred: true);
        }
    }
    private readonly struct MaintainHierarchy(World world) : IJob
    {
        public void Execute() => Hierarchy<Domain>.Maintain(world);
    }
    private readonly struct MaintainRelations<T>(World world) : IJob where T : struct
    {
        public void Execute() => Relations<T>.Maintain(world);
    }
    private readonly struct SwapTargets(World world, RelationEdge<UniqueTargetDirected> first, RelationEdge<UniqueTargetDirected> second,
        Entity sourceA, Entity sourceB, Entity targetA, Entity targetB) : IJob
    {
        public void Execute() => Relations<UniqueTargetDirected>.Edit(world,
            [new(first.Entity, sourceA, targetB), new(second.Entity, sourceB, targetA)], [], deferred: true);
    }
    private readonly struct RetargetUndirected(World world, RelationEdge<UndirectedPayload> edge, Entity first, Entity second) : IJob
    {
        public void Execute() => Relations<UndirectedPayload>.Edit(world, [new(edge.Entity, first, second)], [], deferred: true);
    }
    private readonly struct HoldStructure(ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute() { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
    }
    private readonly struct ReadParentIdentities(Query query, List<Entity> capture) : IJob
    {
        public void Execute()
        {
            using QueryScope rows = query.OpenRead();
            foreach (QueryChunk chunk in rows)
                foreach (Entity entity in chunk.Entities) capture.Add(entity);
        }
    }
    private readonly partial struct CaptureParents(List<(Entity, Entity)> values) : IJobChunk
    {
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(values);
        public void Execute(ReadOnlySpan<Entity> entities, ReadOnlySpan<Parent<Domain>> parents)
        {
            for (int index = 0; index < entities.Length; index++) values.Add((entities[index], parents[index].Value));
        }
    }
    private readonly partial struct CaptureDirected(List<(Entity, Entity, Entity)> values) : IJobChunk
    {
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(values);
        public void Execute(ReadOnlySpan<Entity> entities, ReadOnlySpan<RelationEndpoints<UniqueTargetDirected>> endpoints)
        {
            for (int index = 0; index < entities.Length; index++) values.Add((entities[index], endpoints[index].Source, endpoints[index].Target));
        }
    }
    private readonly partial struct CaptureUndirected(List<(Entity, Entity, Entity)> values) : IJobChunk
    {
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(values);
        public void Execute(ReadOnlySpan<Entity> entities, ReadOnlySpan<RelationEndpoints<UndirectedPayload>> endpoints)
        {
            for (int index = 0; index < entities.Length; index++) values.Add((entities[index], endpoints[index].Source, endpoints[index].Target));
        }
    }
    private readonly struct Domain : IHierarchyDomain;
    private struct EnabledProbe : IEnableableComponent;
    private struct ChunkProbe : IComponent;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private struct UniqueTargetDirected : IComponent;
    [RelationSchema(RelationDirection.Undirected, RelationCardinality.Parallel)]
    private struct UndirectedPayload : IComponent;
}
