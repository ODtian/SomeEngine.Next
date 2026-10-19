using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Tools.RelationsSmoke;

internal static partial class RelationsSmoke
{
    private static int Main()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 2,
            SafetyMode = JobSafetyMode.Checked,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        CheckNumbersAndEdges(jobs);
        CheckSpatialFrames(jobs);
        Console.WriteLine("PASS: packages; arities=1..8; Item8; ref output; schema; typed/generated/query access; edges; key; bitmap; spatial; maintenance; parallel; consumption.");
        return 0;
    }

    private static void CheckNumbersAndEdges(Scheduler jobs)
    {
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var entities = new Entity[8];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.CreateEntity(new Number<int> { Value = i + 1 });
        world.AddTag<Position1>(entities[0]);
        world.AddTag<Position2>(entities[1]);
        world.AddTag<Position3>(entities[2]);
        world.AddTag<Position4>(entities[3]);
        world.AddTag<Position5>(entities[4]);
        world.AddTag<Position6>(entities[5]);
        world.AddTag<Position7>(entities[6]);
        world.AddTag<Position8>(entities[7]);
        var first = world.Query(world.QueryDefinition().All<Position1>());
        var second = world.Query(world.QueryDefinition().All<Position2>());
        var third = world.Query(world.QueryDefinition().All<Position3>());
        var fourth = world.Query(world.QueryDefinition().All<Position4>());
        var fifth = world.Query(world.QueryDefinition().All<Position5>());
        var sixth = world.Query(world.QueryDefinition().All<Position6>());
        var seventh = world.Query(world.QueryDefinition().All<Position7>());
        var eighth = world.Query(world.QueryDefinition().All<Position8>());

        Require(world.Product(first).Where(new Total1(1)).Count() == 1, "unary");
        Require(world.Product(first, second).Where(new Total2(3)).Count() == 1, "binary");
        Require(world.Product(first, second, third).Where(new Total3(6)).Count() == 1, "ternary");
        Require(world.Product(first, second, third, fourth).Where(new Total4(10)).Count() == 1, "four positions");
        Require(world.Product(first, second, third, fourth, fifth).Where(new Total5(15)).Count() == 1, "five positions");
        Require(world.Product(first, second, third, fourth, fifth, sixth).Where(new Total6(21)).Count() == 1, "six positions");
        Require(world.Product(first, second, third, fourth, fifth, sixth, seventh).Where(new Total7(28)).Count() == 1, "seven positions");
        var eight = world.Product(first, second, third, fourth, fifth, sixth, seventh, eighth)
            .Where(new WeightedTotal8(204)).Except(new WeightedTotal8(205));
        Span<Entity> result = stackalloc Entity[1];
        var output = new EighthEntity(result);
        Require(!eight.Enumerate(ref output) && output.Count == 1 && result[0] == entities[7], "eighth identity and output stop");
        Require(eight.Count() == 1 && eight.Any(), "eight-position terminals");

        var numbers = world.Query(world.QueryDefinition().All<Number<int>>().Read<Number<int>>());
        var within = world.Within(numbers).Where(new Total2(14)).Except(new Total2(15));
        var pair = new FirstNumberPair();
        Require(!within.Enumerate(ref pair) && pair.Count == 1 &&
            pair.Value == (entities[5], entities[7]), "within row binding and output stop");
        Require(within.Count() == 1 && within.Any() &&
            world.Within(numbers).Except(new Total2(4)).Count() == 1, "within where and except terminals");
        var point = new NumberPointValue(entities[0]);
        world.ExecuteQuery(numbers, ref point);
        Require(point.Value == 1, "point component reference");
        world.ReleaseQuery(numbers);

        new IncreaseNumbers().Schedule(world).Complete();
        var values = new List<int>();
        ComponentJobAccess<Number<int>>.ScheduleRead(world, new ReadNumber(world, entities[0], values)).Complete();
        Require(values.SequenceEqual([2]), "ordinary typed and generated access");
        bool addressRejected = false;
        try { ComponentJobAccess<NativeAddress<int>>.Read(world); }
        catch (InvalidOperationException) { addressRejected = true; }
        Require(addressRejected, "generated field classification");
        bool queryAddressRejected = false;
        var addressReads = world.QueryDefinition().Read<NativeAddress<int>>().Build();
        try { QueryJobAccess.CopyAccesses(world, addressReads, new JobResourceAccess[QueryJobAccess.Count(addressReads)]); }
        catch (InvalidOperationException) { queryAddressRejected = true; }
        Require(queryAddressRejected, "ordinary query field classification");

        var edge = world.CreateRelation(entities[0], entities[1], new Wire<int>());
        var parallel = world.CreateRelation(entities[0], entities[1], new Wire<int>());
        Require(edge != parallel && world.OutgoingEdges<Wire<int>>(entities[0]).Where(new Total2(5)).Count() == 2, "parallel edge identities");
        Require(world.OutgoingEdges<Wire<int>>(entities[0]).DistinctEndpoints().Count() == 1, "endpoint projection");
        world.RetargetRelationDeferred(edge, entities[0], entities[2]);
        Require(world.EdgesBetween<Wire<int>>(entities[0], entities[1]).Count() == 2, "applied adjacency");
        var targets = new List<Entity>();
        RelationJobAccess<Wire<int>>.ScheduleEndpointsRead(world, new ReadWireTarget(world, edge, targets)).Complete();
        Require(targets.SequenceEqual([entities[2]]), "current generic endpoints in a Job");
        var endpointReads = world.QueryDefinition().Read<DirectedRelationEndpoints<Wire<int>>>().Build();
        Require(QueryJobAccess.CopyAccesses(world, endpointReads, new JobResourceAccess[QueryJobAccess.Count(endpointReads)]) == 2,
            "generated generic endpoint classification");
        RelationMaintenanceSystem<Wire<int>>.Schedule(world).Complete();
        Require(world.IncomingEdges<Wire<int>>(entities[2]).Count() == 1, "maintained adjacency");
        var joint = world.CreateRelation(entities[2], entities[0], new Joint<int>());
        var endpoints = world.GetUndirectedRelationEndpoints(joint);
        Require(endpoints.EndpointA == entities[2] && endpoints.EndpointB == entities[0], "undirected roles");
        Require(RelationSchema.For<Joint<int>>().Cardinality == RelationCardinality.UniquePair, "generated generic schema");
        world.DestroyRelation(parallel);
        Require(world.OutgoingEdges<Wire<int>>(entities[0]).Count() == 1, "edge destruction");
        foreach (var query in new[] { first, second, third, fourth, fifth, sixth, seventh, eighth })
            world.ReleaseQuery(query);
        Console.WriteLine("numbers: 8 arities, Item8, typed/generated reads; edges: identities, roles, maintenance");
    }

    private readonly struct ReadNumber(World world, Entity entity, List<int> values) : IJob
    {
        public void Execute() => values.Add(world.Read<Number<int>>(entity).Value);
    }

    private struct FirstNumberPair : IRelationOutput<(Entity, Entity)>
    {
        public (Entity, Entity) Value;
        public int Count;

        public bool Add(in (Entity, Entity) row)
        {
            Value = row;
            Count++;
            return false;
        }
    }

    private readonly struct ReadWireTarget(World world, RelationEdge<Wire<int>> edge, List<Entity> targets) : IJob
    {
        public void Execute() => targets.Add(world.GetDirectedRelationEndpoints(edge).Target);
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition)
            throw new InvalidOperationException(behavior);
    }
}
