using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class JobBufferCommandTests
{
    [Fact]
    public void CommandBufferOwnsAddReplaceAndRemovePayloads()
    {
        var world = new World();
        Entity entity = world.CreateEntity();
        BufferItem[] source = [new(1), new(2), new(3)];
        using var commands = new CommandBuffer(world);

        commands.AddBuffer<BufferItem>(entity, source);
        source.AsSpan().Fill(new BufferItem(99));
        commands.Playback();

        Assert.Equal([1, 2, 3], Read(world, entity));

        commands.Clear();
        BufferItem[] replacement = [new(7), new(8)];
        commands.ReplaceBuffer<BufferItem>(entity, replacement);
        replacement.AsSpan().Clear();
        commands.Playback();

        Assert.Equal([7, 8], Read(world, entity));

        commands.Clear();
        commands.RemoveBuffer<BufferItem>(entity);
        commands.Playback();

        Assert.False(world.HasBuffer<BufferItem>(entity));
    }

    [Fact]
    public void FailedBufferCommandRollsBackTheCompleteStructuralImage()
    {
        var world = new World();
        Entity first = world.CreateEntity();
        Entity duplicate = world.CreateEntity();
        world.AddBuffer<BufferItem>(duplicate);
        using var commands = new CommandBuffer(world);
        commands.AddBuffer<BufferItem>(first, [new BufferItem(1)]);
        commands.AddBuffer<BufferItem>(duplicate, [new BufferItem(2)]);

        Assert.Throws<InvalidOperationException>(commands.Playback);

        Assert.False(world.HasBuffer<BufferItem>(first));
        Assert.True(world.HasBuffer<BufferItem>(duplicate));
        Assert.Empty(Read(world, duplicate));
    }

    [Fact]
    public void ParallelJobSegmentsPublishPipelineOwnedBuffersByStableProducerKey()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity[] entities = new Entity[8];
        BufferItem[] items = new BufferItem[entities.Length * 2];
        for (int index = 0; index < entities.Length; index++)
        {
            entities[index] = world.CreateEntity();
            items[index * 2] = new BufferItem(index * 10);
            items[index * 2 + 1] = new BufferItem(index * 10 + 1);
        }

        using var commands = new JobCommandBuffer(world, entities.Length);
        var producer = new AddBuffersProducer(entities, items);
        commands.ScheduleParallel(in producer, batchSize: 1).Complete();
        commands.Playback();

        for (int index = 0; index < entities.Length; index++)
        {
            Assert.Equal(
                [index * 10, index * 10 + 1],
                Read(world, entities[index]));
        }
    }

    private static int[] Read(World world, Entity entity)
    {
        int[]? values = null;
        world.ExecuteBufferRead<BufferItem>(entity, buffer =>
        {
            values = new int[buffer.Count];
            for (int index = 0; index < buffer.Count; index++)
                values[index] = buffer[index].Value;
        });
        return values!;
    }

    private readonly struct AddBuffersProducer(
        Entity[] entities,
        BufferItem[] items) : IJobParallelCommandProducer
    {
        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            commands.AddBuffer<BufferItem>(
                entities[producerIndex],
                items.AsSpan(producerIndex * 2, 2));
        }
    }

    private readonly record struct BufferItem(int Value) : IBufferElement;

    private sealed class JobRuntimeScope : IDisposable
    {
        private readonly JobSafetyMode _safety = JobSystem.SafetyMode;
        private readonly ManagedPayloadPolicy _payload = JobSystem.ManagedPayloadPolicy;

        internal JobRuntimeScope(int workerCount)
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = workerCount,
                SafetyMode = _safety,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
            });
        }

        public void Dispose()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = _safety,
                ManagedPayloadPolicy = _payload,
            });
        }
    }
}
