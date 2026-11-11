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
        using var jobs = new Scheduler(new JobConfiguration
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
        using var world = new World(execution: new WorldJobs(jobs));
        var worldJobs = (WorldJobs)world.Execution!;
        var entities = new Entity[8];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new Number<int> { Value = i + 1 });
        world.Set(entities[0], new Position1());
        world.Set(entities[1], new Position2());
        world.Set(entities[2], new Position3());
        world.Set(entities[3], new Position4());
        world.Set(entities[4], new Position5());
        world.Set(entities[5], new Position6());
        world.Set(entities[6], new Position7());
        world.Set(entities[7], new Position8());
        var first = world.Query(new QueryDescription().With<Position1>());
        var second = world.Query(new QueryDescription().With<Position2>());
        var third = world.Query(new QueryDescription().With<Position3>());
        var fourth = world.Query(new QueryDescription().With<Position4>());
        var fifth = world.Query(new QueryDescription().With<Position5>());
        var sixth = world.Query(new QueryDescription().With<Position6>());
        var seventh = world.Query(new QueryDescription().With<Position7>());
        var eighth = world.Query(new QueryDescription().With<Position8>());

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

        var numbers = world.Query(new QueryDescription().With<Number<int>>());
        var within = world.Within(numbers).Where(new Total2(14)).Except(new Total2(15));
        var pair = new FirstNumberPair();
        Require(!within.Enumerate(ref pair) && pair.Count == 1 &&
            pair.Value == (entities[5], entities[7]), "within row binding and output stop");
        Require(within.Count() == 1 && within.Any() &&
            world.Within(numbers).Except(new Total2(4)).Count() == 1, "within where and except terminals");
        using (ReadLookup<Number<int>> point = world.OpenRead<Number<int>>())
            Require(point.Has(entities[0]) && point.ReadKnown(entities[0]).Value == 1, "native point component read");
        new IncreaseNumbers().Schedule(world).Complete();
        var values = new List<int>();
        jobs.Schedule(new ReadNumber(world, entities[0], values), worldJobs.CollectReadAccess(numbers)).Complete();
        Require(values.SequenceEqual([2]), "ordinary typed and generated access");
        bool addressRejected = false;
        try { worldJobs.Read<NativeAddress<int>>(); }
        catch (InvalidOperationException) { addressRejected = true; }
        Require(addressRejected, "generated field classification");
        bool queryAddressRejected = false;
        Query addressReads = world.Query(new QueryDescription().Optional<NativeAddress<int>>());
        try { worldJobs.CollectReadAccess(addressReads); }
        catch (InvalidOperationException) { queryAddressRejected = true; }
        Require(queryAddressRejected, "ordinary query field classification");

        var edge = world.CreateRelation(entities[0], entities[1], new Wire<int>());
        var parallel = world.CreateRelation(entities[0], entities[1], new Wire<int>());
        Require(edge != parallel && world.OutgoingEdges<Wire<int>>(entities[0]).Where(new Total2(5)).Count() == 2, "parallel edge identities");
        Require(world.OutgoingEdges<Wire<int>>(entities[0]).DistinctEndpoints().Count() == 1, "endpoint projection");
        world.RetargetRelationDeferred(edge, entities[0], entities[2]);
        Require(world.EdgesBetween<Wire<int>>(entities[0], entities[1]).Count() == 2, "applied adjacency");
        var targets = new List<Entity>();
        jobs.Schedule(new ReadWireTarget(world, edge, targets), worldJobs.StructureRead).Complete();
        Require(targets.SequenceEqual([entities[2]]), "current generic endpoints in a Job");
        Query endpointReads = world.Query(new QueryDescription().With<RelationEndpoints<Wire<int>>>());
        Require(worldJobs.CollectReadAccess(endpointReads).Length > 0, "generic endpoint storage classification");
        jobs.Schedule(new MaintainWire(world), worldJobs.StructureWrite).Complete();
        Require(world.IncomingEdges<Wire<int>>(entities[2]).Count() == 1, "maintained adjacency");

        var joint = world.CreateRelation(entities[2], entities[0], new Joint<int>());
        var endpoints = world.ReadComponentData<RelationEndpoints<Joint<int>>>(joint.Entity);
        Require(endpoints.Source == entities[2] && endpoints.Target == entities[0], "undirected roles");
        Require(RelationSchema.For<Joint<int>>().Cardinality == RelationCardinality.UniquePair, "generated generic schema");
        world.DestroyRelation(parallel);
        Require(world.OutgoingEdges<Wire<int>>(entities[0]).Count() == 1, "edge destruction");
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
        public void Execute() => targets.Add(world.ReadComponentData<RelationEndpoints<Wire<int>>>(edge.Entity).Target);
    }

    private readonly struct MaintainWire(World world) : IJob
    {
        public void Execute() => Relations<Wire<int>>.Maintain(world);
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition)
            throw new InvalidOperationException(behavior);
    }
}
