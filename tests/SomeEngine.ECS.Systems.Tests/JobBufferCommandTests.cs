using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class JobBufferCommandTests : JobTest
{
    [Fact]
    public void CommandBufferOwnsAddReplaceAndRemovePayloads()
    {
        var world = CreateJobWorld();
        Entity entity = world.Create();
        BufferItem[] source = [new(1), new(2), new(3)];
        using var commands = new CommandBuffer(world);

        commands.SetBuffer<BufferItem>(entity, source);
        source.AsSpan().Fill(new BufferItem(99));
        commands.Playback();

        Assert.Equal([1, 2, 3], Read(world, entity));

        commands.Clear();
        BufferItem[] replacement = [new(7), new(8)];
        commands.SetBuffer<BufferItem>(entity, replacement);
        replacement.AsSpan().Clear();
        commands.Playback();

        Assert.Equal([7, 8], Read(world, entity));

        commands.Clear();
        commands.RemoveBuffer<BufferItem>(entity);
        commands.Playback();

        Assert.False(world.Has<Buffer<BufferItem>>(entity));
    }

    [Fact]
    public void FailedBufferCommandRollsBackTheCompleteStructuralImage()
    {
        var world = CreateJobWorld();
        Entity first = world.Create();
        Entity duplicate = world.Create();
        world.AddBuffer<BufferItem>(duplicate);
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var commands = new CommandBuffer(candidate);
        commands.Add(first, default(Buffer<BufferItem>));
        commands.SetBuffer<BufferItem>(first, [new(1)]);
        commands.Add(duplicate, default(Buffer<BufferItem>));
        commands.SetBuffer<BufferItem>(duplicate, [new(2)]);
        Assert.Throws<InvalidOperationException>(commands.Playback);

        Assert.False(world.Has<Buffer<BufferItem>>(first));
        Assert.True(world.Has<Buffer<BufferItem>>(duplicate));
        Assert.Empty(Read(world, duplicate));
    }

    [Fact]
    public void ParallelJobSegmentsPublishPipelineOwnedBuffersByStableProducerKey()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity[] entities = new Entity[8];
        BufferItem[] items = new BufferItem[entities.Length * 2];
        for (int index = 0; index < entities.Length; index++)
        {
            entities[index] = world.Create();
            items[index * 2] = new BufferItem(index * 10);
            items[index * 2 + 1] = new BufferItem(index * 10 + 1);
        }

        using var commands = new CommandBatch(world, entities.Length);
        var producer = new AddBuffersProducer(commands, entities, items);
        Jobs.ScheduleParallel(in producer, entities.Length, batchSize: 1).Complete();
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
        using ReadOnlyBuffer<BufferItem> buffer = world.ReadBuffer<BufferItem>(entity);
        int[] values = new int[buffer.Count];
        for (int index = 0; index < buffer.Count; index++) values[index] = buffer[index].Value;
        return values;
    }

    private readonly struct AddBuffersProducer(
        CommandBatch commands,
        Entity[] entities,
        BufferItem[] items) : IJobParallelFor
    {
        public void Execute(int producerIndex)
        {
            commands[producerIndex].SetBuffer<BufferItem>(
                entities[producerIndex],
                items.AsSpan(producerIndex * 2, 2));
        }
    }

    private readonly record struct BufferItem(int Value) : IBufferElement;

}
