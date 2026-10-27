using System.Collections.Concurrent;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class ComponentResourceMembershipTests
{
    [Fact]
    public void RemovingDiscoveredComponentRechecksMembershipBeforeExecution()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0,
            SafetyMode = JobSafetyMode.Checked,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var removal = new DiscoveredComponentRemoval(world, scheduler);
        Entity entity = world.CreateEntity(new RemovingDiscoveredComponent(removal));
        removal.Entity = entity;

        new RecordDiscoveredComponent().ScheduleParallel(world).Complete();
        removal.Completion.Complete();

        Assert.False(world.Has<RemovingDiscoveredComponent>(entity));
        string[] events = removal.Events.ToArray();
        Assert.Contains("removed", events);
        Assert.DoesNotContain("executed", events.SkipWhile(value => value != "removed"));
    }
}

public sealed class DiscoveredComponentRemoval(World world, Scheduler scheduler)
{
    public World World { get; } = world;
    public Scheduler Scheduler { get; } = scheduler;
    public Entity Entity;
    public JobHandle Completion;
    public bool Submitted;
    public ConcurrentQueue<string> Events { get; } = new();
}

public readonly record struct RemovingDiscoveredComponent(DiscoveredComponentRemoval Removal) : IComponent;

public readonly struct RemovingDiscoveredComponentResources : IJobComponentResources<RemovingDiscoveredComponent>
{
    public static void Collect(in RemovingDiscoveredComponent component, ref JobResourceAccessBuilder resources)
    {
        DiscoveredComponentRemoval removal = component.Removal;
        resources.Exclusive(removal.Events);
        if (removal.Submitted) return;
        removal.Submitted = true;
        // Submit from a separate thread so the writer has an independent scope. With no workers,
        // its higher priority runs it as soon as discovery releases the World read access.
        removal.Completion = Task.Run(() => removal.Scheduler.Schedule(
            new RemoveDiscoveredComponent(removal),
            RelationshipJobAccess.TopologyWrite(removal.World),
            new JobScheduleOptions(JobPriority.High))).GetAwaiter().GetResult();
    }
}

public partial struct RecordDiscoveredComponent : IJobEntity
{
    public void Execute(Entity entity,
        [JobResources<RemovingDiscoveredComponentResources>] in RemovingDiscoveredComponent component) =>
        component.Removal.Events.Enqueue("executed");
}

public readonly struct RemoveDiscoveredComponent(DiscoveredComponentRemoval removal) : IJob
{
    public void Execute()
    {
        removal.World.Remove<RemovingDiscoveredComponent>(removal.Entity);
        removal.Events.Enqueue("removed");
    }
}
