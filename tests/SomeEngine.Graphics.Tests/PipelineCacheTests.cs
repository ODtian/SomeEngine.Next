using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class PipelineCacheTests
{
    [Fact]
    public void Readiness_failure_identity_and_invalidation_are_deterministic()
    {
        PipelineCacheKey key = new(Guid.Parse("56D272C2-BA70-466F-B9F1-F84427CA83A0"), 7);
        using (var device = new Device())
        {
            (PipelineHandle first, PipelineLayoutHandle layout, ShaderHandle shader) =
                PortableRhiTestSupport.CreateComputePipeline(device, key);
            PipelineHandle second = device.CreateComputePipeline(new ComputePipelineDesc(
                layout,
                shader,
                CacheKey: key));

            Assert.Equal(PipelineStatus.Ready, device.GetPipelineStatus(first));
            Assert.Equal(PipelineStatus.Ready, device.GetPipelineStatus(second));
            PipelineCacheStats hit = device.GetPipelineCacheStats();
            Assert.Equal(1, hit.Entries);
            Assert.Equal(1, hit.Misses);
            Assert.Equal(1, hit.Hits);

            device.InvalidatePipelineCache(key);
            PipelineCacheStats invalidated = device.GetPipelineCacheStats();
            Assert.Equal(0, invalidated.Entries);
            Assert.Equal(1, invalidated.Invalidations);
            device.InvalidateAllPipelines();
            Assert.Equal(invalidated.Invalidations, device.GetPipelineCacheStats().Invalidations);
        }

        using (var pending = new Device(new Options { CreatedPipelineStatus = PipelineStatus.Pending }))
        {
            (PipelineHandle pipeline, _, _) = PortableRhiTestSupport.CreateComputePipeline(pending);
            Assert.Equal(PipelineStatus.Pending, pending.GetPipelineStatus(pipeline));
            using ICommandContext context = pending.AcquireCommandContext(QueueType.Graphics);
            Assert.Throws<InvalidOperationException>(() => context.SetPipeline(pipeline));
        }

        using (var failed = new Device(new Options { CreatedPipelineStatus = PipelineStatus.Failed }))
        {
            (PipelineHandle pipeline, _, _) = PortableRhiTestSupport.CreateComputePipeline(failed);
            Assert.Equal(PipelineStatus.Failed, failed.GetPipelineStatus(pipeline));
            using ICommandContext context = failed.AcquireCommandContext(QueueType.Graphics);
            Assert.Throws<InvalidOperationException>(() => context.SetPipeline(pipeline));
        }
    }

}
