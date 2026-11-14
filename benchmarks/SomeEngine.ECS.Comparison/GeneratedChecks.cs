using System.Buffers;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

[GenerateComponent, GenerateCodec]
public partial record struct GeneratedCounter(int Value, int Ordinal) : IAddedComponent;
[GenerateComponent, GenerateCodec]
public partial record struct GeneratedOther(int Value) : IAddedComponent;
[GenerateCodec]
public readonly partial record struct GeneratedVelocity(int Value) : IEnableableComponent;
[GenerateCodec]
public readonly partial record struct GeneratedElement(int Value) : IAddedComponent;
[GenerateComponent, GenerateCodec]
public readonly partial record struct GeneratedShared(int Value) : ISharedComponent;
[GenerateComponent, GenerateCodec]
public readonly partial record struct GeneratedLinks(Entity Target, Entity Outside, Entity[] Links, int[] Weights, string? Text);
[GenerateComponent, GenerateCodec]
public readonly partial record struct GeneratedEnvelope(GeneratedLinks Value, int Revision);

public readonly partial struct GeneratedAdvance : IJobEntity
{
    public void Execute(Entity entity, ref GeneratedCounter counter, ref GeneratedOther other, in GeneratedVelocity velocity)
    { counter.Value += velocity.Value + entity.Id; other.Value += counter.Ordinal; }
}

public readonly partial struct GeneratedVectorAdvance : IJobChunk
{
    public void Execute(ReadOnlySpan<GeneratedVelocity> velocities, Span<GeneratedOther> values, ReadOnlySpan<Entity> entities)
    { for (int i = 0; i < entities.Length; i++) values[i].Value += velocities[i].Value; }
}

public readonly partial struct GeneratedAppend : IJobEntity
{
    public void Execute(Entity entity, DynamicBuffer<GeneratedElement> buffer, in GeneratedVelocity velocity)
        => buffer.Add(new(entity.Id + velocity.Value));
}

[JobWrite(typeof(GeneratedOther))]
public readonly partial struct GeneratedRandomWrite(World world, Entity target) : IJobEntity
{
    private readonly World _world = world;
    private readonly Entity _target = target;
    public void Execute(ref GeneratedCounter counter)
    { counter.Value++; _world.Get<GeneratedOther>(_target).Value++; }
}

public readonly partial struct GeneratedOverwrite<T>(T value) : IJobEntity where T : unmanaged
{
    private readonly T _value = value;
    public void Execute(ref T target) => target = _value;
}

internal static class GeneratedChecks
{
    internal static void Run()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        CheckJobs(scheduler, false); CheckJobs(scheduler, true); CheckValues();
        Console.WriteLine("Generated consumers: entity/chunk work, multiple tracked writes, row identities, enablement, buffers, random write policy, generic jobs, system groups, codecs and owned references passed.");
    }

    private static void CheckJobs(Scheduler scheduler, bool connected)
    {
        using var world = new World(512, chunkCapacity: 128, execution: connected ? new WorldJobs(scheduler) : null);
        var entities = new Entity[512];
        for (int i = 0; i < entities.Length; i++)
        {
            Entity entity = entities[i] = world.Spawn(new GeneratedRow { Counter = new(i, i), Other = new(), Velocity = new(2) });
            world.AddBuffer<GeneratedElement>(entity);
            if (i % 4 == 0) world.Disable<GeneratedVelocity>(entity);
        }
        var job = new GeneratedAdvance();
        ulong before = world.Version; job.Execute(world);
        Require(world.Version == before + 1, "one generated batch version covers both tracked component columns");
        Require(world.Query(new QueryDescription().WithChanged<GeneratedCounter>(before)).Count == 384
            && world.Query(new QueryDescription().WithChanged<GeneratedOther>(before)).Count == 384,
            "generated writes publish both exact selected row sets");
        int steps = 1;
        if (connected)
        {
            job.ExecuteParallel(world); steps++;
            job.Schedule(world).Complete(); steps++;
            JobHandle first = job.ScheduleParallel(world);
            job.ScheduleParallel(world, first).Complete(); steps += 2;
            var group = new SystemGroup(world, scheduler, 128);
            SystemState a = group.Add(job), b = group.Add(job); group.After(b, a);
            group.Execute(); steps += 2;
            Require(group.GraphBuildCount == 1 && a.LastSystemVersion == a.CurrentSystemVersion
                && b.LastSystemVersion == b.CurrentSystemVersion, "generated systems publish completed graph baselines");
        }
        for (int i = 0; i < entities.Length; i++)
        {
            int count = i % 4 == 0 ? 0 : steps;
            Require(world.ReadValue<GeneratedCounter>(entities[i]).Value == i + count * (2 + entities[i].Id)
                && world.ReadValue<GeneratedOther>(entities[i]).Value == count * i,
                "generated row bodies use each entity's actual identity and both writable columns");
        }
        before = world.Version;
        var vectors = new GeneratedVectorAdvance();
        if (connected) vectors.ExecuteParallel(world); else vectors.Execute(world);
        Require(world.Version == before + 1 && world.Query(new QueryDescription().WithChanged<GeneratedOther>(before)).Count == 384,
            "generated chunk spans retain their query filter and tracked completion");
        before = world.Version;
        var buffers = new GeneratedAppend();
        if (connected) buffers.ScheduleParallel(world).Complete(); else buffers.Execute(world);
        Require(world.Version == before + 1 && world.Query(new QueryDescription().WithChangedBuffer<GeneratedElement>(before)).Count == 384,
            "generated buffer writes share the declared batch version and completed summaries");
        for (int i = 0; i < entities.Length; i++)
        {
            using var buffer = world.ReadBuffer<GeneratedElement>(entities[i]);
            Require(buffer.Count == (i % 4 == 0 ? 0 : 1) && (buffer.Count == 0 || buffer[0].Value == entities[i].Id + 2),
                "generated buffer parameters retain each row's owned contents");
        }
        before = world.Version; int old = world.ReadValue<GeneratedOther>(entities[0]).Value;
        var random = new GeneratedRandomWrite(world, entities[0]);
        if (connected) random.ScheduleParallel(world).Complete(); else random.Execute(world);
        Require(world.Version == before + 1 && world.ReadValue<GeneratedOther>(entities[0]).Value == old + entities.Length
            && world.Query(new QueryDescription().WithChanged<GeneratedOther>(before)).Count == 1,
            "generated random writes select one execution item and retain one tracked batch version");
        new GeneratedOverwrite<GeneratedOther>(new(42)).Execute(world);
        foreach (Entity entity in entities) Require(world.ReadValue<GeneratedOther>(entity).Value == 42,
            "generated generic jobs bind their closed component type");
    }

    private static void CheckValues()
    {
        using var world = new World(); Entity outside = world.Create();
        Entity first = world.Create(), second = world.Create();
        world.Set(first, new GeneratedLinks(second, outside, [first, second, outside], [3, 5], "first"));
        world.Set(second, new GeneratedLinks(first, outside, [second, first, outside], [7, 11], null));
        world.Set(first, new GeneratedEnvelope(new GeneratedLinks(second, outside, [first, second, outside], [17, 19], "nested"), 3));
        world.SetShared(first, new GeneratedShared(7));
        Entity[] copied = new Entity[2]; world.Clone([first, second], copied);
        GeneratedLinks copy = world.ReadValue<GeneratedLinks>(copied[0]);
        Require(copy.Target == copied[1] && copy.Outside == outside
            && copy.Links.AsSpan().SequenceEqual([copied[0], copied[1], outside]), "generated entity fields and arrays remap copied references");
        copy.Weights[0] = 99;
        Require(world.ReadValue<GeneratedLinks>(first).Weights[0] == 3, "generated value operations clone independent owned arrays");
        GeneratedEnvelope envelope = world.ReadValue<GeneratedEnvelope>(copied[0]);
        Require(envelope.Value.Target == copied[1] && envelope.Value.Links.AsSpan().SequenceEqual([copied[0], copied[1], outside]),
            "generated value operations remap nested entity fields");
        envelope.Value.Weights[0] = 88;
        Require(world.ReadValue<GeneratedEnvelope>(first).Value.Weights[0] == 17, "generated nested value operations clone independent owned arrays");
        using var checkpoint = new WorldCheckpoint(); checkpoint.Capture(world);
        copy.Weights[1] = 101; checkpoint.Restore(world);
        Require(world.ReadValue<GeneratedLinks>(copied[0]).Weights[1] == 5, "generated value operations participate in world data capture");
        var registry = GeneratedLinks.RegisterCodec(new SerializationRegistry(), SaveSchema.Key(81, "GeneratedLinks"));
        GeneratedShared.RegisterCodec(registry, SaveSchema.Key(82, "GeneratedShared"));
        GeneratedEnvelope.RegisterCodec(registry, SaveSchema.Key(83, "GeneratedEnvelope"));
        var serializer = new WorldSerializer(registry); var bytes = new ArrayBufferWriter<byte>();
        serializer.Save(world, bytes, SavePurpose.DurableSave, CheckpointProgress.Preserve);
        world.Set(first, new GeneratedLinks(default, default, [], [], "changed"));
        serializer.Load(world, bytes.WrittenSpan);
        GeneratedLinks loaded = world.ReadValue<GeneratedLinks>(first);
        Require(loaded.Target == second && loaded.Outside == outside && loaded.Links.AsSpan().SequenceEqual([first, second, outside])
            && loaded.Weights.AsSpan().SequenceEqual([3, 5]) && loaded.Text == "first"
            && world.ReadValue<GeneratedLinks>(second).Text is null && world.ReadValue<GeneratedShared>(first).Value == 7,
            "generated field codecs restore all values, shared values and entity arrays");
        envelope = world.ReadValue<GeneratedEnvelope>(first);
        Require(envelope.Value.Target == second && envelope.Value.Weights.AsSpan().SequenceEqual([17, 19]) && envelope.Revision == 3,
            "generated field codecs compose nested generated values");

        using var commands = new CommandBuffer(world);
        DeferredEntity target = commands.Create(new GeneratedOther(5));
        DeferredEntity link = commands.Create<GeneratedLinks, GeneratedLinks.EntityFields>(new(default, outside, [], [13], "command"),
            [new(GeneratedLinks.EntityFields.TargetField, target)]);
        commands.Playback();
        Require(world.ReadValue<GeneratedLinks>(link.Resolve()).Target == target.Resolve(),
            "generated entity field identifiers patch producer-local command references");
    }

    private static void Require(bool condition, string behavior)
    { if (!condition) throw new InvalidOperationException(behavior); }
}
