using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Tests;

public sealed class FieldRangeTests
{
    [Fact]
    public void IndependentReferencedFieldsWriteTogetherThroughTheirDeclaredResources()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var reached = new CountdownEvent(2);
        var firstData = new ReferenceValue { Reached = reached };
        var secondData = new ReferenceValue { Reached = reached };
        var first = world.CreateEntity(new ReferenceRoot { Data = firstData });
        var second = world.CreateEntity(new ReferenceRoot { Data = secondData });
        var channel = Channel<float>.Of<ReferenceRoot, ReferenceAccess, ReferenceResources>(1);
        uint firstVersion = world.WriteVersion<ReferenceRoot>(first);
        uint secondVersion = world.WriteVersion<ReferenceRoot>(second);
        FieldWrite<float>[] writes = [new(first, channel, 3), new(second, channel, 7)];

        channel.WriteBatch(world, writes);

        Assert.Equal(3f, firstData.Value);
        Assert.Equal(7f, secondData.Value);
        Assert.True(world.WriteVersion<ReferenceRoot>(first) > firstVersion);
        Assert.True(world.WriteVersion<ReferenceRoot>(second) > secondVersion);
    }

    [Fact]
    public void GeneratedReferenceFieldsWriteTogetherWithOneComponentVersion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var reached = new CountdownEvent(2);
        var firstData = new ReferenceValue { Reached = reached };
        var secondData = new ReferenceValue { Reached = reached };
        var first = world.CreateEntity(new ReferenceRoot { Data = firstData });
        var second = world.CreateEntity(new ReferenceRoot { Data = secondData });
        var channel = FieldRangeTests_ReferenceRootChannels.Value;
        FieldWrite<float>[] writes = [new(first, channel, 3), new(second, channel, 7)];

        channel.WriteBatch(world, writes);

        Assert.Equal(3f, firstData.Value);
        Assert.Equal(7f, secondData.Value);
        Assert.Equal(world.WriteVersion<ReferenceRoot>(first), world.WriteVersion<ReferenceRoot>(second));
    }

    [Fact]
    public void AliasedReferenceTargetsKeepTheInputWriteOrder()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var data = new ReferenceValue();
        var first = world.CreateEntity(new ReferenceRoot { Data = data });
        var second = world.CreateEntity(new ReferenceRoot { Data = data });
        var channel = FieldRangeTests_ReferenceRootChannels.Value;
        FieldWrite<float>[] writes = [new(second, channel, 7), new(first, channel, 3)];

        channel.WriteBatch(world, writes);

        Assert.Equal(new[] { 7f, 3f }, data.Writes);
        Assert.Equal(3f, data.Value);
    }

    [Fact]
    public void ReplacingTheRootReferenceWritesTheCurrentObjectForItsResourceConsumer()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var previous = new ReferenceValue();
        var current = new ReferenceValue();
        var target = world.CreateEntity(new ReferenceRoot { Data = previous });
        var channel = FieldRangeTests_ReferenceRootChannels.Value;
        channel.WriteBatch(world, [new(target, channel, 3)]);
        world.Replace(target, new ReferenceRoot { Data = current });

        channel.WriteBatch(world, [new(target, channel, 7)]);

        float[] result = [0];
        scheduler.Schedule(new ReadReference(current, result),
            [JobResourceAccess.Read(scheduler.GetResourceToken(current))]).Complete();
        Assert.Equal(3f, previous.Value);
        Assert.Equal(7f, result[0]);
    }

    private readonly record struct ReadReference(ReferenceValue Data, float[] Result) : IJob
    {
        public void Execute() => Result[0] = Data.Value;
    }

    [Fact]
    public void DisjointFieldsInOneArrayWriteTogetherThroughTheirActualRanges()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var reached = new CountdownEvent(2);
        float[] values = [0, 0];
        var first = world.CreateEntity(new ArrayRoot { Values = values, Index = 0, Reached = reached });
        var second = world.CreateEntity(new ArrayRoot { Values = values, Index = 1, Reached = reached });
        var channel = Channel<float>.Of<ArrayRoot, ArrayAccess, ArrayResources>(1);

        channel.WriteBatch(world, [new(first, channel, 3), new(second, channel, 7)]);

        Assert.Equal(new[] { 3f, 7f }, values);
    }

    private struct ArrayRoot : IComponent
    {
        public float[] Values;
        public int Index;
        public CountdownEvent Reached;
    }

    private readonly struct ArrayAccess : IFieldAccess<ArrayRoot, float>
    {
        public static float Read(in ArrayRoot component) => component.Values[component.Index];
        public static void Write(ref ArrayRoot component, in float value)
        {
            component.Reached.Signal();
            if (!component.Reached.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            component.Values[component.Index] = value;
        }
    }

    private readonly struct ArrayResources : IJobComponentResources<ArrayRoot>
    {
        public static void Collect(in ArrayRoot component, ref JobResourceAccessBuilder resources) =>
            resources.Write(component.Values.AsMemory(component.Index, 1));
    }

    public sealed class ReferenceValue
    {
        internal CountdownEvent? Reached;
        internal readonly List<float> Writes = [];
        private float _value;
        public float Value { get => _value; set => Write(value); }

        internal void Write(float value)
        {
            if (Reached is { } reached)
            {
                reached.Signal();
                if (!reached.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException();
            }
            _value = value;
            Writes.Add(value);
        }
    }

    [GenerateFields]
    [ComponentField("Data.Value", Name = "Value")]
    public struct ReferenceRoot : IComponent
    {
        public ReferenceValue Data;
    }

    private readonly struct ReferenceAccess : IFieldAccess<ReferenceRoot, float>
    {
        public static float Read(in ReferenceRoot component) => component.Data.Value;
        public static void Write(ref ReferenceRoot component, in float value) => component.Data.Write(value);
    }

    private readonly struct ReferenceResources : IJobComponentResources<ReferenceRoot>
    {
        public static void Collect(in ReferenceRoot component, ref JobResourceAccessBuilder resources) => resources.Write(component.Data);
    }
}
