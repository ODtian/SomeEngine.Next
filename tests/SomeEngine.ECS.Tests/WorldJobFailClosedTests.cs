using SomeEngine.Job;
using Xunit;

namespace SomeEngine.ECS.Tests;

/// <summary>A standalone World requires its explicit Job access coordinator before Job execution.</summary>
public sealed class WorldJobFailClosedTests
{
    [Fact]
    public void RawJobRequiresAnExplicitWorldJobsConnection()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var world = new World();
        ulong version = world.Version;
        JobHandle handle = scheduler.Schedule(new RawWorldJob(world));

        Assert.Throws<InvalidOperationException>(() => handle.Complete());
        Assert.Equal(0, world.EntityCount);
        Assert.Equal(version, world.Version);
    }

    private readonly struct RawWorldJob(World world) : IJob
    {
        public void Execute()
        {
            _ = world.Create();
        }
    }
}
