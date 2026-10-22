using SomeEngine.ECS.Entities;
using SomeEngine.Job;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class GeneratedJobResourceTests : JobTest
{
    [Fact]
    public void ScheduleSamplesJobResourceAfterItsDeclaredWriter()
    {
        World world = CreateJobWorld();
        Entity entity = world.CreateEntity(new ResourceSample { Index = 1 });
        var heights = new HeightSamples([2, 5, 8]);
        JobResourceToken data = Jobs.GetResourceToken(heights.Values);
        JobHandle written = Jobs.Schedule(
            new SetHeight(heights.Values, 1, 13),
            JobResourceAccess.Write(data));

        new SampleHeight(heights).Schedule(
            world,
            [JobResourceAccess.Read(data)],
            written).Complete();

        Assert.Equal(13, world.Read<ResourceSample>(entity).Value);
    }

    [Fact]
    public void ScheduleParallelSamplesSharedJobResourceIntoEachEntity()
    {
        World world = CreateJobWorld();
        Entity[] entities = Enumerable.Range(0, 4)
            .Select(index => world.CreateEntity(new ResourceSample { Index = index }))
            .ToArray();
        var heights = new HeightSamples([11, 22, 33, 44]);

        new SampleHeight(heights).ScheduleParallel(
            world,
            [JobResourceAccess.Read(Jobs.GetResourceToken(heights.Values))],
            new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();

        Assert.Equal([11, 22, 33, 44], entities.Select(entity => world.Read<ResourceSample>(entity).Value));
    }

    [Fact]
    public void JobsWritingSeparateResourceRangesRunTogether()
    {
        World firstWorld = CreateJobWorld();
        World secondWorld = CreateJobWorld();
        Entity first = firstWorld.CreateEntity(new ResourceSample { Index = 0 });
        Entity second = secondWorld.CreateEntity(new ResourceSample { Index = 1 });
        int[] values = new int[2];
        JobResourceToken data = Jobs.GetResourceToken(values);
        using var together = new Barrier(2);

        JobHandle firstWritten = new WriteHeightRange(values, together, 11).ScheduleParallel(
            firstWorld,
            [JobResourceAccess.Write(data, 0, 1)]);
        JobHandle secondWritten = new WriteHeightRange(values, together, 22).ScheduleParallel(
            secondWorld,
            [JobResourceAccess.Write(data, 1, 1)]);
        firstWritten.Complete();
        secondWritten.Complete();

        Assert.Equal([11, 22], new[]
        {
            firstWorld.Read<ResourceSample>(first).Value,
            secondWorld.Read<ResourceSample>(second).Value,
        });
    }

    [Fact]
    public void JobResourceStaysAliveUntilItsAttachedChildCompletes()
    {
        World world = CreateJobWorld();
        Entity entity = world.CreateEntity(new ResourceSample());
        using var fence = new ResourceChildFence();
        (JobHandle handle, WeakReference resource) = ScheduleResourceChild(world, fence);
        try
        {
            Assert.True(fence.Registered.Wait(TimeSpan.FromSeconds(5)));
            _ = world.Read<ResourceSample>(entity);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.True(resource.IsAlive);
        }
        finally
        {
            fence.Signal();
            handle.Complete();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (JobHandle, WeakReference) ScheduleResourceChild(World world, ResourceChildFence fence)
    {
        var heights = new HeightSamples([17]);
        var resource = new WeakReference(heights);
        JobHandle handle = new SampleHeightWithChild(heights, fence).ScheduleParallel(
            world,
            [JobResourceAccess.Read(Jobs.GetResourceToken(heights.Values))]);
        return (handle, resource);
    }

    private readonly record struct SetHeight(int[] Values, int Index, int Value) : IJob
    {
        public void Execute() => Values[Index] = Value;
    }
}

internal partial struct WriteHeightRange(int[] values, Barrier together, int value) : IJobEntity
{
    private readonly int[] _values = values;
    private readonly Barrier _together = together;
    private readonly int _value = value;

    public void Execute(ref ResourceSample sample)
    {
        _values[sample.Index] = _value;
        if (!_together.SignalAndWait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The independent resource writes did not meet.");
        sample.Value = _values[sample.Index];
    }
}

internal sealed class ResourceChildFence : IJobExternalFence, IDisposable
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ManualResetEventSlim Registered { get; } = new();
    public bool IsSignaled => _completion.Task.IsCompleted;

    public void OnSignaled(Action<object?> continuation, object? state)
    {
        if (IsSignaled)
            continuation(state);
        else
            _completion.Task.GetAwaiter().OnCompleted(() => continuation(state));
        Registered.Set();
    }

    internal void Signal() => _completion.SetResult();
    public void Dispose() => Registered.Dispose();
}

internal partial struct SampleHeightWithChild(HeightSamples heights, ResourceChildFence fence) : IJobEntity
{
    private readonly HeightSamples _heights = heights;
    private readonly ResourceChildFence _fence = fence;

    public void Execute(ref ResourceSample sample)
    {
        sample.Value = _heights.Values[sample.Index];
        JobSystem.CreateExternalFenceHandle(_fence);
    }
}

public sealed class HeightSamples(int[] values)
{
    public int[] Values { get; } = values;
}

public struct ResourceSample : IComponent
{
    public int Index;
    public int Value;
}

public partial struct SampleHeight(HeightSamples heights) : IJobEntity
{
    private readonly HeightSamples _heights = heights;

    public void Execute(ref ResourceSample sample)
    {
        sample.Value = _heights.Values[sample.Index];
    }
}
