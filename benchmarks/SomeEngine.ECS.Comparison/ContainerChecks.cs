using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal static class ContainerChecks
{
    internal static void Run()
    {
        var releases = new ReleaseCount();
        using var world = new World();
        var source = world.Create(new Position());
        world.AddBuffer<Element>(source);
        ulong before = world.Version;
        using (var buffer = world.GetBuffer<Element>(source))
        {
            for (int i = 0; i < 4; i++) buffer.Add(new Element { Value = i });
            buffer.Insert(2, new Element { Value = 9 });
            buffer.RemoveAt(2);
            buffer.AddRange([new Element { Value = 4 }, new Element { Value = 5 }]);
        }
        Query changed = world.Query(new QueryDescription().WithChangedBuffer<Element>(before));
        using (var scope = changed.Open())
        {
            int count = 0;
            foreach (var rows in scope) count += rows.Count;
            Require(count == 1, "buffer write views publish one component change");
        }
        var copy = world.Clone(source);
        using (var buffer = world.GetBuffer<Element>(source)) buffer[0].Value = 42;
        world.Add(source, new Extra());
        using (var original = world.ReadBuffer<Element>(source))
        using (var cloned = world.ReadBuffer<Element>(copy))
            Require(original.Count == 6 && original[0].Value == 42 && cloned.Count == 6 && cloned[0].Value == 0,
                "migration preserves buffer contents and clone owns independent elements");
        var variableBuffers = new SomeEngine.ECS.Entities.Entity[2];
        var repeatedBuffers = new SomeEngine.ECS.Entities.Entity[2];
        Position[] positions = [new() { X = 10 }, new() { X = 11 }];
        Buffer<Element> template = world.Read<Buffer<Element>>(source);
        Buffer<Element>[] inputs = [template, template];
        world.Create<Position, Buffer<Element>, Velocity, Health>(variableBuffers, positions, inputs, new Velocity(), new Health());
        Velocity[] velocities = [new(), new()];
        world.Create<Position, Velocity, Health, Buffer<Element>>(repeatedBuffers, positions, velocities, new Health(), template);
        using (var first = world.GetBuffer<Element>(variableBuffers[0])) first[0].Value = 70;
        using (var first = world.GetBuffer<Element>(repeatedBuffers[0])) first[0].Value = 80;
        using (var variable = world.ReadBuffer<Element>(variableBuffers[1]))
        using (var repeated = world.ReadBuffer<Element>(repeatedBuffers[1]))
        using (var original = world.ReadBuffer<Element>(source))
            Require(variable.Count == 6 && repeated.Count == 6 && variable[0].Value == 42
                && repeated[0].Value == 42 && original[0].Value == 42,
                "batch variable and repeated owned values keep independent buffer elements");
        ulong rangeBefore = world.Version;
        using (var scope = world.Query<Buffer<Element>>().Open())
            foreach (var rows in scope)
            {
                var buffers = rows.GetBuffers<Element>();
                for (int row = 0; row < buffers.Count; row++)
                {
                    using var buffer = buffers[row];
                    buffer[buffer.Count - 1].Value = 90;
                }
            }
        Require(world.Query(new QueryDescription().WithChangedBuffer<Element>(rangeBefore)).Count == 6
            && world.Query(new QueryDescription().WithAddedBuffer<Element>(rangeBefore)).Count == 0,
            "buffer row writes mark the complete range and preserve original Added versions");
        world.Destroy(variableBuffers);
        world.Destroy(repeatedBuffers);
        world.RemoveBuffer<Element>(source);
        using (var old = world.ReadRemovedBuffer<Element>(source))
            Require(old.Count == 6 && old[0].Value == 42, "removed facts take over the old buffer contents");
        world.ClearRemoved<Buffer<Element>>();
        world.AddBuffer<Element>(source);
        using (var empty = world.ReadBuffer<Element>(source)) Require(empty.Count == 0, "returned buffer storage starts a fresh lifetime");
        world.SetResource(new ResourceCounter(releases, 1));
        world.SetResource(new ResourceCounter(releases, 2));
        Require(world.ReadResource<ResourceCounter>().Value == 2 && releases.Count == 1, "resource replacement releases the previous value");
        world.RemoveResource<ResourceCounter>();
        Require(releases.Count == 2 && !world.HasResource<ResourceCounter>(), "resource removal completes release");
        world.SetResource(new ResourceCounter(releases, 3));
        world.SetSparse(source, new ResourceCounter(releases, 4));
        world.Clear();
        Require(releases.Count == 4, "world clear releases resources and sparse owned values");
        world.SetResource(new ResourceCounter(releases, 5));
        world.Dispose();
        Require(releases.Count == 5, "world dispose releases the last resource once");
        CheckScopedReadAccess();
        Console.WriteLine("Buffer views, deep copy, removed contents and resource release behavior: passed.");
    }

    private static void CheckScopedReadAccess()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 1, EnableCounters = false });
        CheckScopedReadAccess(null);
        CheckScopedReadAccess(new WorldJobs(scheduler));
        Console.WriteLine("Plain and connected scoped reads: ordinary/shared/removed values, missing-value exceptions and paired release passed.");
    }

    private static void CheckScopedReadAccess(WorldExecution? execution)
    {
        using var world = new World(execution: execution);
        Entity entity = world.Create(new Position { X = 17 });
        Entity missing = world.Create(new Extra());
        world.SetShared(entity, new ScopedShared(9));
        world.AddBuffer<Element>(entity);
        using (var buffer = world.GetBuffer<Element>(entity)) buffer.Add(new Element { Value = 42 });
        world.RemoveBuffer<Element>(entity);
        ulong before = world.Version;

        Require(ReadOrdinary(world, entity).X == 17 && ReadSharedValue(world, entity).Value == 9,
            "scoped ordinary and shared reads keep the original values");
        using (var removed = ReadRemoved(world, entity))
            Require(removed.Count == 1 && removed[0].Value == 42, "scoped removed buffer retains its owned values");

        bool ordinaryMissing = false, sharedMissing = false, removedMissing = false;
        try { _ = ReadOrdinary(world, missing); } catch (ArgumentException) { ordinaryMissing = true; }
        try { _ = ReadSharedValue(world, missing); } catch (ArgumentException) { sharedMissing = true; }
        try { using var absent = ReadRemoved(world, missing); } catch (ArgumentException) { removedMissing = true; }
        Require(ordinaryMissing && sharedMissing && removedMissing, "missing scoped values preserve their exceptions");
        Require(world.Version == before, "successful and failed reads do not publish a write version");
        using (world.OpenStructureWrite())
            Require(ReadOrdinary(world, entity).X == 17 && ReadSharedValue(world, entity).Value == 9,
                "read failures release access before a subsequent structure-write scope");
        Entity after = world.Create(new Position { X = 23 });
        Require(ReadOrdinary(world, after).X == 23, "failed removed reads leave no borrowed structure read behind");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Position ReadOrdinary(World world, Entity entity) => world.ReadValue<Position>(entity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ScopedShared ReadSharedValue(World world, Entity entity) => world.ReadValue<ScopedShared>(entity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReadOnlyBuffer<Element> ReadRemoved(World world, Entity entity) => world.ReadRemovedBuffer<Element>(entity);

    private readonly record struct ScopedShared(int Value) : ISharedComponent;

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
    private struct Element : IAddedComponent, IRemovedComponent { internal int Value; }
    private sealed class ReleaseCount { internal int Count; }
    private readonly struct ResourceCounter(ReleaseCount releases, int value) : IDisposable
    {
        internal int Value { get; } = value;
        public void Dispose() => releases.Count++;
    }
}
