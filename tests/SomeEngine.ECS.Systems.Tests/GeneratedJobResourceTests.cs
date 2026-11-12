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
        Entity entity = world.Create(new ResourceSample { Index = 1 });
        var heights = new HeightSamples([2, 5, 8]);
        JobHandle written = Jobs.Schedule(
            new SetHeight(heights.Values, 1, 13),
            JobResourceAccess.Write(Jobs, heights.Values.AsMemory(1, 1)));

        new SampleHeight(heights, 1, 1).Schedule(world, written).Complete();

        Assert.Equal(13, world.ReadComponentData<ResourceSample>(entity).Value);
    }

    [Fact]
    public void ScheduleParallelSamplesSharedJobResourceIntoEachEntity()
    {
        World world = CreateJobWorld();
        Entity[] entities = Enumerable.Range(0, 4)
            .Select(index => world.Create(new ResourceSample { Index = index }))
            .ToArray();
        var heights = new HeightSamples([11, 22, 33, 44]);

        new SystemRunner(world, rangeRows: 1).Schedule(new SampleHeight(heights), parallel: true).Complete();

        Assert.Equal([11, 22, 33, 44], entities.Select(entity => world.ReadComponentData<ResourceSample>(entity).Value));
    }

    [Fact]
    public void JobsWritingSeparateResourceRangesRunTogether()
    {
        World firstWorld = CreateJobWorld();
        World secondWorld = CreateJobWorld();
        Entity first = firstWorld.Create(new ResourceSample { Index = 0 });
        Entity second = secondWorld.Create(new ResourceSample { Index = 1 });
        int[] values = new int[2];
        using var together = new Barrier(2);

        JobHandle firstWritten = new WriteHeightRange(values, together, 11, 0).ScheduleParallel(firstWorld);
        JobHandle secondWritten = new WriteHeightRange(values, together, 22, 1).ScheduleParallel(secondWorld);
        firstWritten.Complete();
        secondWritten.Complete();

        Assert.Equal([11, 22], new[]
        {
            firstWorld.ReadComponentData<ResourceSample>(first).Value,
            secondWorld.ReadComponentData<ResourceSample>(second).Value,
        });
    }

    [Fact]
    public void JobResourceStaysAliveUntilItsAttachedChildCompletes()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create(new ResourceSample());
        using var fence = new ResourceChildFence();
        (JobHandle handle, WeakReference resource) = ScheduleResourceChild(world, fence);
        try
        {
            Assert.True(fence.Registered.Wait(TimeSpan.FromSeconds(5)));
            _ = world.ReadComponentData<ResourceSample>(entity);
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
        JobHandle handle = new SampleHeightWithChild(heights, fence).ScheduleParallel(world);
        return (handle, resource);
    }

    private readonly record struct SetHeight(int[] Values, int Index, int Value) : IJob
    {
        public void Execute() => Values[Index] = Value;
    }
}

internal partial struct WriteHeightRange(int[] values, Barrier together, int value, int index) : IJobEntity
{
    private readonly int[] _values = values;
    private readonly Barrier _together = together;
    private readonly int _value = value;
    public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(_values.AsMemory(index, 1));

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
    public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Read(_heights.Values.AsMemory());

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

public partial struct SampleHeight(HeightSamples heights, int start = 0, int count = -1) : IJobEntity
{
    private readonly HeightSamples _heights = heights;
    public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) =>
        resources.Read(_heights.Values.AsMemory(start, count < 0 ? _heights.Values.Length - start : count));

    public void Execute(ref ResourceSample sample)
    {
        sample.Value = _heights.Values[sample.Index];
    }
}
