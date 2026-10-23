using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class EdgeRelationTests
{
    [Fact]
    public void AdjacencyQueriesKeepParallelEdgeIdentityAndRoleLocalOrder()
    {
        using var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        world.SetRelationAdjacencyOrder<Shipment>(source, RelationAdjacencyRole.Outgoing, RelationAdjacencyOrderPolicy.Ordered);
        world.SetRelationAdjacencyOrder<Shipment>(target, RelationAdjacencyRole.Incoming, RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, target, new Shipment());
        var second = world.CreateRelation(source, target, new Shipment());
        world.ReorderRelationAdjacency(source, RelationAdjacencyRole.Outgoing, second, 0);
        var outgoing = new List<RelationEdge<Shipment>>();
        var incoming = new List<RelationEdge<Shipment>>();
        var outgoingOutput = new EdgeList<Shipment>(outgoing);
        var incomingOutput = new EdgeList<Shipment>(incoming);

        world.OutgoingEdges<Shipment>(source).Enumerate(ref outgoingOutput);
        world.IncomingEdges<Shipment>(target).Enumerate(ref incomingOutput);

        Assert.Equal(new[] { second, first }, outgoing);
        Assert.Equal(new[] { first, second }, incoming);
        Assert.Equal(2, world.OutgoingEdges<Shipment>(source).Count());
        Assert.True(world.IncomingEdges<Shipment>(target).Any());
    }

    [Fact]
    public void EndpointRulesKeepEdgeRowsAndForwardEarlyOutputState()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var source = world.CreateEntity(new Score { Value = 1 });
        var target = world.CreateEntity(new Weight { Value = 8 });
        var heavy = world.CreateEntity(new Weight { Value = 12 });
        var first = world.CreateRelation(source, target, new Shipment());
        world.CreateRelation(source, target, new Shipment());
        world.CreateRelation(source, heavy, new Shipment());
        var selected = world.OutgoingEdges<Shipment>(source)
            .Where(new ScoreWeightGap(5))
            .Except(new ScoreWeightGap(9));
        Span<RelationEdge<Shipment>> result = stackalloc RelationEdge<Shipment>[1];
        var output = new FirstEdge<Shipment>(result);

        bool completed = selected.Enumerate(ref output);

        Assert.False(completed);
        Assert.Equal(1, output.Count);
        Assert.Equal(first, result[0]);
        Assert.Equal(2, selected.Count());
        Assert.True(selected.Any());
    }

    private ref struct FirstEdge<T> : IRelationOutput<RelationEdge<T>>
        where T : struct, IComponent
    {
        private readonly Span<RelationEdge<T>> _result;

        public FirstEdge(Span<RelationEdge<T>> result)
        {
            _result = result;
            Count = 0;
        }

        public int Count { get; private set; }

        public bool Add(in RelationEdge<T> edge)
        {
            _result[Count++] = edge;
            return false;
        }
    }

    [Fact]
    public void RetargetingAndMaintenanceUpdateEdgeMembershipAndCurrentEndpointRules()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var source = world.CreateEntity(new Score { Value = 1 });
        var target = world.CreateEntity(new Weight { Value = 8 });
        var replacement = world.CreateEntity(new Weight { Value = 3 });
        var otherSource = world.CreateEntity(new Score { Value = 0 });
        var edge = world.CreateRelation(source, target, new Shipment());
        var oldPair = world.EdgesBetween<Shipment>(source, target);
        var single = world.Edge(edge);

        world.RetargetRelationDeferred(edge, source, replacement);

        Assert.Equal(1, oldPair.Count());
        Assert.Equal(0, oldPair.Where(new ScoreWeightGap(5)).Count());
        Assert.Equal(0, single.Where(new ScoreWeightGap(5)).Count());

        RelationMaintenanceSystem<Shipment>.Schedule(world).Complete();

        Assert.Equal(0, oldPair.Count());
        Assert.Equal(1, world.EdgesBetween<Shipment>(source, replacement).Count());

        world.RetargetRelationImmediate(edge, otherSource, target);

        Assert.Equal(0, world.OutgoingEdges<Shipment>(source).Count());
        Assert.Equal(1, world.EdgesBetween<Shipment>(otherSource, target).Count());

        world.DestroyRelation(edge);

        Assert.False(single.Any());
        Assert.Equal(0, world.IncomingEdges<Shipment>(target).Count());
    }

    [Fact]
    public void IncidentRulesKeepUndirectedEndpointArgumentRoles()
    {
        using var world = new World();
        var first = world.CreateEntity(new Score { Value = 1 });
        var second = world.CreateEntity(new Weight { Value = 8 });
        var forward = world.CreateRelation(first, second, new Connection());
        var reverse = world.CreateRelation(second, first, new Connection());
        var matching = new List<RelationEdge<Connection>>();
        var other = new List<RelationEdge<Connection>>();
        var matchingOutput = new EdgeList<Connection>(matching);
        var otherOutput = new EdgeList<Connection>(other);

        world.IncidentEdges<Connection>(first).Where(new ScoreWeightGap(5)).Enumerate(ref matchingOutput);
        world.IncidentEdges<Connection>(second).Except(new ScoreWeightGap(5)).Enumerate(ref otherOutput);

        Assert.Equal(new[] { forward }, matching);
        Assert.Equal(new[] { reverse }, other);
        Assert.Equal(2, world.EdgesBetween<Connection>(first, second).Count());
    }

    [Fact]
    public void EndpointProjectionsDeduplicateDeclaredPairOrderAfterMaintenance()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var first = world.CreateEntity(new Score { Value = 1 });
        var second = world.CreateEntity(new Weight { Value = 8 });
        world.SetRelationAdjacencyOrder<Connection>(first, RelationAdjacencyRole.Incident, RelationAdjacencyOrderPolicy.Ordered);
        world.CreateRelation(first, second, new Connection(), RelationMaintenanceTiming.Deferred);
        world.CreateRelation(first, second, new Connection(), RelationMaintenanceTiming.Deferred);
        world.CreateRelation(second, first, new Connection(), RelationMaintenanceTiming.Deferred);
        var pairs = new List<(Entity, Entity)>();
        var endpoints = world.IncidentEdges<Connection>(first).DistinctEndpoints();

        var maintenance = RelationMaintenanceSystem<Connection>.Schedule(world);
        var reader = new EndpointPairsReader(endpoints, pairs);
        RelationJobAccess<Connection>.ScheduleEndpointsRead(world, reader, maintenance).Complete();

        Assert.Equal(new[] { (first, second), (second, first) }, pairs);
        Assert.Equal(3, world.IncidentEdges<Connection>(first).Count());
        Assert.Equal(2, endpoints.Count());
        Assert.Equal(1, endpoints.Where(new ScoreWeightGap(5)).Count());
        Assert.Equal(1, world.IncidentEdges<Connection>(first).DistinctUnorderedEndpoints().Count());
    }

    private readonly struct EndpointPairsReader(
        RelationQuery<DistinctEdgeEndpoints<IncidentRelationEdges<Connection>, Connection>, (Entity, Entity)> query,
        List<(Entity, Entity)> pairs) : IJob
    {
        public void Execute()
        {
            var output = new PairList(pairs);
            query.Enumerate(ref output);
        }
    }

    private struct PairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            pairs.Add(pair);
            return true;
        }
    }

    private struct EdgeList<T>(List<RelationEdge<T>> edges) : IRelationOutput<RelationEdge<T>>
        where T : struct, IComponent
    {
        public bool Add(in RelationEdge<T> edge)
        {
            edges.Add(edge);
            return true;
        }
    }
}

[RelationSchema(RelationDirection.Directed)]
public struct Shipment : IComponent;

[RelationSchema(RelationDirection.Undirected)]
public struct Connection : IComponent;
