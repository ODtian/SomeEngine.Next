using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.ECS.Spatial;

namespace SomeEngine.Tools.RelationsSmoke;

internal static partial class RelationsSmoke
{
    private static void CheckSpatialFrames(Scheduler jobs)
    {
        using var world = new World(execution: new WorldJobs(jobs));
        var worldJobs = (WorldJobs)world.Execution!;
        var firstCharacter = world.Create();
        var secondCharacter = world.Create();
        var a = world.Create(new Body { Motion = MotionKind.Dynamic, Bounds = Box(0), Categories = new(1, 1) });
        var b = world.Create(new Body { Motion = MotionKind.Dynamic, Bounds = Box(0.2f), Categories = new(1, 1) });
        var c = world.Create(new Body { Motion = MotionKind.Dynamic, Bounds = Box(0.4f), Categories = new(1, 1) });
        var d = world.Create(new Body { Motion = MotionKind.Static, Bounds = Box(0.6f), Categories = new(1, 1) });
        world.Set(a, new Hair { Character = firstCharacter });
        world.Set(b, new Hair { Character = firstCharacter });
        world.Set(c, new Hair { Character = secondCharacter });
        var bodies = world.Query(new QueryDescription().With<Body>());
        var ordinary = world.Within(bodies).Where(default(DefaultContacts)).Except(default(SameCharacterHair));
        var indexed = world.Within(bodies).Except(default(SameCharacterHair), default(HairCharacterIndex)).Where(default(DefaultContacts));
        Require(ordinary.Count() == 5 && indexed.Count() == 5, "key implementation matches component semantics");
        Require(world.Within(bodies).Except(default(SameCharacterHair), default(HairCharacterIndex)).Count() == 5,
            "key membership-only output");
        Require(world.Within(bodies).Where(default(MutualCategories), default(BodyBitmap)).Count() == 6, "bitmap intersection");
        Require(world.Within(bodies).Where(default(MutualCategories), default(ForwardedBodyBitmap)).Count() == 6,
            "custom permission result callback");

        var contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies,
                SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default), default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(BodyBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var pairs = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 5);
        using var serial = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 5);
        using var totals = new ContactTotals(jobs, 32);
        var consumed = ScheduleFrame(maintenance, pairs, totals, maintenance.Schedule());
        consumed.Complete();
        contacts.FillPairs(serial);
        Require(pairs.Count == 5 && pairs.Pairs.ToArray().ToHashSet().SetEquals([(a, c), (a, d), (b, c), (b, d), (c, d)]), "first parallel pairs");
        Require(serial.Pairs.ToArray().ToHashSet().SetEquals(pairs.Pairs.ToArray()), "serial and parallel pairs");
        Require(new[] { totals.Counts[a.Id], totals.Counts[b.Id], totals.Counts[c.Id], totals.Counts[d.Id] }
            .SequenceEqual([2, 2, 3, 3]), "first consumed contacts");
        Require(totals.Depths[a.Id] > 0 && totals.Depths[d.Id] > 0, "geometric penetration consumption");

        world.AcquireSystemVersion();
        var writer = new UpdateBodies(world, b, c, secondCharacter);
        JobResourceAccess[] writes = worldJobs.CollectAccess(new SystemAccesses().WriteRandom<Body>(), structureWrite: true);
        var updated = jobs.Schedule(writer, writes, consumed);
        consumed = ScheduleFrame(maintenance, pairs, totals, maintenance.Schedule(updated));
        consumed.Complete();
        Require(pairs.Count == 3 && pairs.Pairs.ToArray().ToHashSet().SetEquals([(a, b), (a, d), (b, d)]), "updated parallel pairs");
        Require(new[] { totals.Counts[a.Id], totals.Counts[b.Id], totals.Counts[c.Id], totals.Counts[d.Id] }
            .SequenceEqual([2, 2, 0, 2]), "updated consumed contacts");
        Require(contacts.Any() && contacts.Count() == 3, "maintained terminals");

        world.Create(Box(20));
        world.Create(Box(20.5f));
        var bounds = world.Query(new QueryDescription().With<SpatialBounds>());
        Require(SpatialQueries.Candidates(world, bounds).Count() == 1, "default bounds package API");
        Console.WriteLine("spatial: key+bitmap+components; parallel/serial=5; updated=3; consumed counts=2,2,0,2");
    }

    private static SpatialBounds Box(float x) => new(new Vector3(x - 0.5f, -0.5f, -0.5f), new Vector3(x + 0.5f, 0.5f, 0.5f));

    private static JobHandle ScheduleFrame<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        SpatialMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        SpatialPairBuffer<BoundsNodePair> pairs, ContactTotals totals, JobHandle dependency)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
    {
        var fill = new FillContacts<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>>(maintenance.Query, pairs);
        var filled = maintenance.ScheduleRead(fill, [pairs.FillAccess], dependency);
        var consume = new ConsumeContacts(maintenance.Query.Enumerable.World, pairs, totals);
        return maintenance.ScheduleRead(consume, [pairs.ReadAccess, totals.WriteAccess], filled);
    }

    private readonly struct FillContacts<TEnumerable>(RelationQuery<TEnumerable, (Entity, Entity)> query,
        SpatialPairBuffer<BoundsNodePair> pairs) : IJob where TEnumerable : struct, ISpatialPartitionEnumerable<BoundsNodePair>
    {
        public void Execute() => query.FillPairsParallel(pairs);
    }

    private readonly struct ConsumeContacts(World world, SpatialPairBuffer<BoundsNodePair> pairs, ContactTotals totals) : IJob
    {
        public void Execute()
        {
            Array.Clear(totals.Counts);
            Array.Clear(totals.Depths);
            using ReadLookup<Body> bodies = world.OpenRead<Body>();
            foreach (var pair in pairs.Pairs)
            {
                var first = bodies.ReadKnown(pair.Item1).Bounds;
                var second = bodies.ReadKnown(pair.Item2).Bounds;
                var overlap = Vector3.Min(first.Max, second.Max) - Vector3.Max(first.Min, second.Min);
                float depth = MathF.Min(overlap.X, MathF.Min(overlap.Y, overlap.Z));
                if (depth < 0)
                    continue;
                totals.Counts[pair.Item1.Id]++;
                totals.Counts[pair.Item2.Id]++;
                totals.Depths[pair.Item1.Id] += depth;
                totals.Depths[pair.Item2.Id] += depth;
            }
        }
    }

    private readonly struct UpdateBodies(World world, Entity hair, Entity body, Entity character) : IJob
    {
        public void Execute()
        {
            world.Set(hair, new Hair { Character = character });
            var value = world.Read<Body>(body);
            value.Categories = new CategoryBits(2, 2);
            world.Set(body, value);
        }
    }
}

internal sealed class ContactTotals : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly JobResource _resource;

    internal ContactTotals(Scheduler scheduler, int capacity)
    {
        _scheduler = scheduler;
        _resource = scheduler.CreateResource("Package consumer contacts");
        Counts = new int[capacity];
        Depths = new float[capacity];
    }

    internal int[] Counts { get; }
    internal float[] Depths { get; }
    internal JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);

    public void Dispose() => _scheduler.ReleaseResource(_resource);
}
