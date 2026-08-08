using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

public sealed class WorldStructuralMetricsTests
{
    [Fact]
    public void MetricsSeparatePublishedAndAbortedStructuralCandidates()
    {
        var world = new World();
        using (var successful = new CommandBuffer(world))
        {
            DeferredEntity entity = successful.CreateEntity();
            successful.Add(entity, new MetricsProbe { Value = 1 });
            successful.Playback();
        }

        Entity target = world.CreateEntity();
        using (var failing = new CommandBuffer(world))
        {
            failing.Remove<MetricsProbe>(target);
            Assert.Throws<InvalidOperationException>(() => failing.Playback());
        }

        WorldStructuralMetrics metrics = world.GetStructuralMetrics();
        Assert.Equal(2, metrics.Started);
        Assert.Equal(1, metrics.Published);
        Assert.Equal(1, metrics.Aborted);
        Assert.True(metrics.PrepareTime >= TimeSpan.Zero);
        Assert.True(metrics.MaximumPrepareTime >= TimeSpan.Zero);
        Assert.True(metrics.CommitTime >= TimeSpan.Zero);
        Assert.True(metrics.Lifetime >= metrics.PrepareTime);
        Assert.True(metrics.ClonedArchetypeShells > 0);
        Assert.True(metrics.MaximumClonedArchetypeShells > 0);
        Assert.True(metrics.ClonedChunkShells >= metrics.MaximumClonedChunkShells);
        Assert.True(metrics.ClonedQueryMatches >= metrics.MaximumClonedQueryMatches);
    }

    private struct MetricsProbe : IComponent
    {
        internal int Value;
    }
}
