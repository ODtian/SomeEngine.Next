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
    private static World? s_world;

    [Fact]
    public void RawJobFailsClosedWhenAdmissionIntegrationWasNeverLoaded()
    {
        var world = new World();
        long revision = world.PublishedTopologyRevision;
        s_world = world;
        try
        {
            JobHandle handle = JobSystem.Schedule(new RawWorldJob());

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => handle.Complete());
            Assert.Contains("admission", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, world.EntityCount);
            Assert.Equal(revision, world.PublishedTopologyRevision);
        }
        finally
        {
            s_world = null;
        }
    }

    private readonly struct RawWorldJob : IJob
    {
        public void Execute()
        {
            _ = s_world!.CreateEntity();
        }
    }
}
