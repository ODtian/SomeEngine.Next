using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class GeneratedComponentResourceTests : JobTest
{
    [Fact]
    public void ParallelSamplingUsesReplacedComponentResourcesAndNewMembers()
    {
        World world = CreateJobWorld();
        var first = new HeightSamples([2, 5]);
        var second = new HeightSamples([9, 12]);
        Entity[] entities =
        [
            AddSample(world, first, 0),
            AddSample(world, first, 1),
            AddSample(world, second, 0),
            AddSample(world, second, 1),
        ];
        var options = new JobEntityScheduleOptions(rowsPerPacket: 1);
        new SampleComponentHeight().ScheduleParallel(world, options).Complete();

        var replacement = new HeightSamples([30, 40, 50]);
        world.Replace(entities[0], new ReferencedHeight(replacement, 0));
        world.Replace(entities[1], new ReferencedHeight(replacement, 1));
        Entity added = AddSample(world, replacement, 2);
        new SampleComponentHeight().ScheduleParallel(world, options).Complete();

        Assert.Equal([30, 40, 9, 12, 50], entities.Append(added)
            .Select(entity => world.Read<SampledHeight>(entity).Value));
    }

    [Fact]
    public void ParallelGroupsCoordinateSharedWritesAndRunIndependentRangesTogether()
    {
        World world = CreateJobWorld();
        int[] values = [0, 0];
        using var meeting = new Barrier(2);
        Entity[] entities =
        [
            world.CreateEntity(new ReferencedRange(values, 0, meeting, 0)),
            world.CreateEntity(new ReferencedRange(values, 0, null, 0)),
            world.CreateEntity(new ReferencedRange(values, 1, meeting, 0)),
        ];

        new IncrementComponentRange().ScheduleParallel(
            world, new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();

        Assert.Equal([2, 1, 1, 1, 1], values.Concat(
            entities.Select(entity => world.Read<ReferencedRange>(entity).Updates)));
    }

    [Fact]
    public void SamplingDiscoversReferencesAfterItsReplacementDependency()
    {
        World world = CreateJobWorld();
        var original = new HeightSamples([3]);
        var replacement = new HeightSamples([99]);
        Entity entity = AddSample(world, original, 0);
        using var fence = new ResourceChildFence();
        JobHandle ready = Jobs.CreateExternalFenceHandle(fence);
        JobHandle replaced = new ReplaceComponentHeight(replacement).Schedule(world, ready);
        JobHandle sampled = new SampleComponentHeight().ScheduleParallel(world, replaced);

        fence.Signal();
        sampled.Complete();

        Assert.Equal(99, world.Read<SampledHeight>(entity).Value);
    }

    [Fact]
    public void GenericJobClosesItsComponentResourceDescriptor()
    {
        World world = CreateJobWorld();
        Entity entity = world.CreateEntity(new ReferencedMemory<int>(new[] { 7, 31 }, 1));
        world.Add(entity, new MemorySample<int>());

        new SampleComponentMemory<int>().ScheduleParallel(world).Complete();

        Assert.Equal(31, world.Read<MemorySample<int>>(entity).Value);
    }

    private static Entity AddSample(World world, HeightSamples samples, int index)
    {
        Entity entity = world.CreateEntity(new ReferencedHeight(samples, index));
        world.Add(entity, new SampledHeight());
        return entity;
    }
}

public readonly record struct ReferencedMemory<T>(ReadOnlyMemory<T> Values, int Index) : IComponent
    where T : unmanaged;

public struct MemorySample<T> : IComponent where T : unmanaged
{
    public T Value;
}

public readonly struct MemoryResources<T> : IJobComponentResources<ReferencedMemory<T>> where T : unmanaged
{
    public static void Collect(in ReferencedMemory<T> component, ref JobResourceAccessBuilder resources) =>
        resources.Read(component.Values);
}

public partial struct SampleComponentMemory<T> : IJobEntity where T : unmanaged
{
    public void Execute(
        [JobResources(typeof(MemoryResources<>))] in ReferencedMemory<T> source,
        ref MemorySample<T> result) => result.Value = source.Values.Span[source.Index];
}

public record struct ReferencedRange(int[] Values, int Offset, Barrier? Meeting, int Updates) : IComponent;

public readonly struct WriteRangeResources : IJobComponentResources<ReferencedRange>
{
    public static void Collect(in ReferencedRange component, ref JobResourceAccessBuilder resources) =>
        resources.Write(component.Values.AsMemory(component.Offset, 1));
}

public partial struct IncrementComponentRange : IJobEntity
{
    public void Execute([JobResources<WriteRangeResources>] ref ReferencedRange component)
    {
        int value = component.Values[component.Offset];
        if (component.Meeting is { } meeting && !meeting.SignalAndWait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The independent resource ranges did not meet.");
        component.Values[component.Offset] = value + 1;
        component.Updates++;
    }
}

public readonly record struct ReferencedHeight(HeightSamples Samples, int Index) : IComponent;

public struct SampledHeight : IComponent
{
    public int Value;
}

public readonly struct ReadHeightResources : IJobComponentResources<ReferencedHeight>
{
    public static void Collect(in ReferencedHeight component, ref JobResourceAccessBuilder resources) =>
        resources.Read(new ReadOnlyMemory<int>(component.Samples.Values));
}

public readonly struct HeightReferenceResources : IJobComponentResources<ReferencedHeight>
{
    public static void Collect(in ReferencedHeight component, ref JobResourceAccessBuilder resources) { }
}

public partial struct ReplaceComponentHeight(HeightSamples replacement) : IJobEntity
{
    private readonly HeightSamples _replacement = replacement;

    public void Execute([JobResources<HeightReferenceResources>] ref ReferencedHeight source) =>
        source = new ReferencedHeight(_replacement, source.Index);
}

public partial struct SampleComponentHeight : IJobEntity
{
    public void Execute(
        [JobResources<ReadHeightResources>] in ReferencedHeight source,
        ref SampledHeight result) => result.Value = source.Samples.Values[source.Index];
}
