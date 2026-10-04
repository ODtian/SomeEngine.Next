using SomeEngine.Job;
using Xunit;

namespace SomeEngine.ECS.Tests;

/// <summary>
/// This test project deliberately does not reference SomeEngine.ECS.Systems. It proves that a
/// raw Job cannot become an unadmitted synchronous World caller merely because the optional
/// integration assembly was never loaded and its module initializer never ran.
/// </summary>
public sealed class WorldJobFailClosedTests
{
    [Fact]
    public void RawJobFailsClosedWhenAdmissionIntegrationWasNeverLoaded()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var world = new World();
        long revision = world.PublishedTopologyRevision;
        JobHandle handle = scheduler.Schedule(new RawWorldJob(world));

        Assert.Throws<InvalidOperationException>(() => handle.Complete());
        Assert.Equal(0, world.EntityCount);
        Assert.Equal(revision, world.PublishedTopologyRevision);
    }

    private readonly struct RawWorldJob(World world) : IJob
    {
        public void Execute()
        {
            _ = world.CreateEntity();
        }
    }
}
