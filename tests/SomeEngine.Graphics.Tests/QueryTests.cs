using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class QueryTests
{
    [Fact]
    public void Query_pool_metadata_validates_exact_domain_and_generation()
    {
        using Device device = new();
        using Device other = new();
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.PipelineStatistics, 3));
        Assert.Equal(
            new QueryPoolMetadata(QueryType.PipelineStatistics, 3, PipelineStatisticsValues.ByteSize),
            device.GetQueryPoolMetadata(pool));
        Assert.Throws<ArgumentException>(() => other.GetQueryPoolMetadata(pool));
        device.DestroyQueryPool(pool);
        Assert.Throws<InvalidOperationException>(() => device.GetQueryPoolMetadata(pool));
    }

    [Fact]
    public void Timestamp_write_resolve_and_bounds_are_validated()
    {
        using var device = new Device();
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.Timestamp, 2));
        BufferHandle readback = PortableRhiTestSupport.CreateReadback(device, 32);

        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            context.ResetQueryPool(pool, 0, 2);
            context.WriteTimestamp(pool, 0);
            context.WriteTimestamp(pool, 1);
            context.ResolveQueryPool(pool, 0, 2, readback, 0, 16);
            Submit(device, context, QueueType.Graphics);
        }

        byte[] values = new byte[32];
        device.ReadBuffer(readback, 0, values);
        ulong first = PortableRhiTestSupport.ReadUInt64(values, 0);
        ulong second = PortableRhiTestSupport.ReadUInt64(values, 16);
        Assert.True(first > 0);
        Assert.True(second > first);

        BufferHandle wrongUsage = device.CreateBuffer(new BufferDesc(32, BufferUsage.CopySource));
        using ICommandContext validation = device.AcquireCommandContext(QueueType.Graphics);
        Assert.Throws<ArgumentOutOfRangeException>(() => validation.WriteTimestamp(pool, 2));
        Assert.Throws<InvalidOperationException>(() => validation.BeginQuery(pool, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => validation.ResetQueryPool(pool, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            validation.ResolveQueryPool(pool, 0, 2, readback, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            validation.ResolveQueryPool(pool, 0, 2, readback, 4, 16));
        Assert.Throws<InvalidOperationException>(() =>
            validation.ResolveQueryPool(pool, 0, 2, wrongUsage, 0));
    }

    [Fact]
    public void Occlusion_requires_render_scope_balanced_begin_end_and_valid_resolve()
    {
        using var device = new Device();
        (PipelineHandle pipeline, _, _, _) = PortableRhiTestSupport.CreateRasterPipeline(device);
        (TextureHandle target, TextureViewHandle view) = PortableRhiTestSupport.CreateRenderTarget(device);
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.Occlusion, 1));
        BufferHandle readback = PortableRhiTestSupport.CreateReadback(device, sizeof(ulong));

        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            context.Barriers([ResourceBarrier.Transition(target.Resource, ResourceState.Common, ResourceState.RenderTarget)]);
            context.ResetQueryPool(pool, 0, 1);
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            context.BeginQuery(pool, 0);
            context.Draw(3);
            context.EndQuery(pool, 0);
            context.EndRendering();
            context.ResolveQueryPool(pool, 0, 1, readback, 0);
            Submit(device, context, QueueType.Graphics);
        }

        byte[] result = new byte[sizeof(ulong)];
        device.ReadBuffer(readback, 0, result);
        Assert.Equal(1UL, PortableRhiTestSupport.ReadUInt64(result));

        using (ICommandContext outside = device.AcquireCommandContext(QueueType.Graphics))
        {
            Assert.Throws<InvalidOperationException>(() => outside.BeginQuery(pool, 0));
            Assert.Throws<InvalidOperationException>(() => outside.EndQuery(pool, 0));
        }

        using (ICommandContext unbalanced = device.AcquireCommandContext(QueueType.Graphics))
        {
            unbalanced.BeginRendering(PortableRhiTestSupport.Rendering(view));
            unbalanced.SetPipeline(pipeline);
            unbalanced.BeginQuery(pool, 0);
            Assert.Throws<InvalidOperationException>(() => unbalanced.EndRendering());
            unbalanced.EndQuery(pool, 0);
            unbalanced.EndRendering();
        }
    }

    [Fact]
    public void Pipeline_statistics_validate_scope_layout_and_resolve_stride()
    {
        using var device = new Device();
        (PipelineHandle pipeline, _, _) = PortableRhiTestSupport.CreateComputePipeline(device);
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.PipelineStatistics, 1));
        BufferHandle readback = PortableRhiTestSupport.CreateReadback(device, 96);

        using (ICommandContext context = device.AcquireCommandContext(QueueType.Compute))
        {
            context.ResetQueryPool(pool, 0, 1);
            context.SetPipeline(pipeline);
            context.BeginQuery(pool, 0);
            context.Dispatch(2, 3, 1);
            context.EndQuery(pool, 0);
            context.ResolveQueryPool(pool, 0, 1, readback, 0, 96);
            Submit(device, context, QueueType.Compute);
        }

        byte[] result = new byte[checked((int)PipelineStatisticsValues.ByteSize)];
        device.ReadBuffer(readback, 0, result);
        Assert.Equal(6UL, PortableRhiTestSupport.ReadUInt64(result, 10 * sizeof(ulong)));

        using (ICommandContext validation = device.AcquireCommandContext(QueueType.Compute))
        {
            Assert.Throws<InvalidOperationException>(() => validation.BeginQuery(pool, 0));
            validation.SetPipeline(pipeline);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                validation.ResolveQueryPool(pool, 0, 1, readback, 0, 80));
        }
    }

    [Fact]
    public void Null_timestamp_frequency_and_calibration_share_one_clock_domain()
    {
        using var device = new Device();
        TimestampCalibration before = device.GetTimestampCalibration(QueueType.Graphics);
        Assert.Equal(device.GetTimestampFrequency(QueueType.Graphics), before.TimestampFrequency);
        Assert.Equal(before.CpuTimestamp, before.GpuTimestamp);

        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.Timestamp, 1));
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            context.WriteTimestamp(pool, 0);
            Submit(device, context, QueueType.Graphics);
        }

        TimestampCalibration after = device.GetTimestampCalibration(QueueType.Graphics);
        Assert.Equal(after.CpuTimestamp, after.GpuTimestamp);
        Assert.True(after.GpuTimestamp > before.GpuTimestamp);
        Assert.Equal(before.TimestampFrequency, after.TimestampFrequency);
        using var limited = new Device(new Options { SupportsCopyQueue = false });
        Assert.Throws<NotSupportedException>(() => limited.GetTimestampFrequency(QueueType.Copy));
    }

    private static void Submit(Device device, ICommandContext context, QueueType queue)
    {
        CommandListHandle list = context.Finish();
        GpuCompletion completion = device.Submit(queue, [list]);
        Assert.True(device.Wait(completion, TimeSpan.Zero));
    }
}
