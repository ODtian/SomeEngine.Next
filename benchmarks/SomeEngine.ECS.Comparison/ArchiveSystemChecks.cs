using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal static partial class ArchiveSystemChecks
{
    private readonly partial struct Advance : IJobChunk
    {
        public void Execute(Span<SaveCounter> counters, ReadOnlySpan<Position> positions)
        { foreach (ref SaveCounter counter in counters) counter.Value++; }
    }

    internal static void Run()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        using var world = new World(128, chunkCapacity: 64, execution: new WorldJobs(scheduler));
        var entities = new Entity[128];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.Spawn(new CounterPosition { Counter = new(2, i), Position = new() { X = i } });
        var group = new SystemGroup(world, scheduler, 64);
        group.Add(new Advance(), world.Query<SaveCounter, Position>());
        group.Add(new Advance(), world.Query<SaveCounter, Position>());
        var registry = new SerializationRegistry()
            .Register<SaveCounter, SaveSchema.CounterCodec>(SaveSchema.Key(1, "SystemCounter"))
            .Register<Position, SaveSchema.PositionCodec>(SaveSchema.Key(2, "SystemPosition"));
        var serializer = new WorldSerializer(registry);
        using var snapshot = new WorldCheckpoint();

        JobHandle pending = group.Schedule();
        snapshot.Capture(world, CheckpointProgress.Preserve, group);
        Require(pending.IsCompleted, "memory capture completes the bound system group");
        ulong[] versions = Versions(group);
        Check(world, entities, group, versions, 4);
        pending = group.Schedule();
        snapshot.Restore(world, group);
        Require(pending.IsCompleted, "memory restore completes the previous bound system run");
        Check(world, entities, group, versions, 4);

        pending = group.Schedule();
        var bytes = new ArrayBufferWriter<byte>();
        serializer.Save(world, bytes, SavePurpose.RawCheckpoint, CheckpointProgress.Preserve, group);
        Require(pending.IsCompleted, "binary save completes the bound system group");
        versions = Versions(group);
        pending = group.Schedule();
        serializer.Load(world, bytes.WrittenSpan, null, group);
        Require(pending.IsCompleted, "binary load completes the previous bound system run");
        Check(world, entities, group, versions, 6);

        pending = group.Schedule();
        using var stream = new MemoryStream();
        Task saving = serializer.SaveAsync(world, stream, SavePurpose.DurableSave, CheckpointProgress.Preserve, group);
        Require(pending.IsCompleted, "background save completes system progress during caller capture");
        versions = Versions(group);
        world.Set(entities[0], new SaveCounter(999, 0));
        saving.GetAwaiter().GetResult();
        stream.Position = 0;
        serializer.Load(world, stream, null, group);
        Check(world, entities, group, versions, 8);
        Console.WriteLine("Actual system group archives: completion, all row values, both system baselines and caller-captured background progress passed.");
        CheckFullWorlds(scheduler);
    }

    private static void CheckFullWorlds(Scheduler scheduler)
    {
        foreach (bool raw in new[] { true, false })
        foreach (ArchiveComparison.Library library in ArchiveComparison.Libraries(128, raw, scheduler))
        {
            using FullArchiveWorld world = library.Create();
            using ArchiveWorldGroup group = world.ConnectArchiveGroup(scheduler);
            JobHandle pending = group.Schedule();
            using IDisposable snapshot = world.Capture(true);
            Require(pending.IsCompleted, "complete world capture drains the actual bound group");
            ulong[] versions = Versions(group);
            pending = group.Schedule(); world.Restore(snapshot);
            Require(pending.IsCompleted, "complete world memory restore drains the current bound group");
            CheckPositions(world, group, versions, 2);
            pending = group.Schedule(); var bytes = new ArrayBufferWriter<byte>(); world.Save(bytes, true);
            Require(pending.IsCompleted, "complete world binary save drains the actual bound group");
            versions = Versions(group);
            pending = group.Schedule(); world.Load(bytes.WrittenSpan);
            Require(pending.IsCompleted, "complete world binary load drains the current bound group");
            CheckPositions(world, group, versions, 4);
            pending = group.Schedule(); using var output = new MemoryStream(); Task saving = world.SaveAsync(output, true);
            Require(pending.IsCompleted, "complete background capture drains the actual bound group on the caller");
            versions = Versions(group); pending = group.Schedule(); saving.GetAwaiter().GetResult();
            output.Position = 0; world.Load(output);
            Require(pending.IsCompleted, "complete background restore drains the current bound group");
            CheckPositions(world, group, versions, 6);
            Console.WriteLine(library.Name + ": complete archive raw=" + raw + " restores actual group rows and both system version pairs.");
        }
    }

    private static void CheckPositions(FullArchiveWorld world, ArchiveWorldGroup group, ulong[] versions, int steps)
    {
        for (int i = 0; i < 128; i++)
        {
            Position position = world.ReadPosition(i);
            Require(position.X == i + steps && position.Y == steps * 2, "complete bound archive restores each actual system row");
        }
        Require(Versions(group).AsSpan().SequenceEqual(versions), "complete bound archive restores both actual system version pairs");
    }

    private static ulong[] Versions(ISystemVersions group)
    {
        var values = new ulong[group.VersionCount * 2]; group.CopyVersions(values);
        for (int i = 0; i < values.Length; i += 2)
            Require(values[i] == values[i + 1], "each completed system publishes its current observation point");
        return values;
    }

    private static void Check(World world, Entity[] entities, SystemGroup group, ulong[] versions, int expected)
    {
        foreach (Entity entity in entities)
            Require(world.ReadValue<SaveCounter>(entity).Value == expected, "captured system rows restore together");
        Require(Versions(group).AsSpan().SequenceEqual(versions), "captured system version pairs restore together");
    }

    private static void Require(bool condition, string behavior)
    { if (!condition) throw new InvalidOperationException(behavior); }
}
