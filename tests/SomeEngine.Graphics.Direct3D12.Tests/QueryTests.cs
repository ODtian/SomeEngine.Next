using System.Buffers.Binary;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class QueryTests
{
    [Fact]
    public void Warp_query_metadata_and_discarded_recordings_preserve_submitted_availability()
    {
        Assert.True(OperatingSystem.IsWindows());
        using Device device = CreateDevice();
        using Device other = CreateDevice();
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.Timestamp, 1));
        Assert.Equal(new QueryPoolMetadata(QueryType.Timestamp, 1, sizeof(ulong)), device.GetQueryPoolMetadata(pool));
        Assert.Throws<ArgumentException>(() => other.GetQueryPoolMetadata(pool));
        BufferHandle readback = device.CreateBuffer(new BufferDesc(8, BufferUsage.CopyDestination), MemoryType.Readback);

        using (ICommandContext discarded = device.AcquireCommandContext(QueueType.Graphics))
        {
            discarded.WriteTimestamp(pool, 0);
            device.DiscardCommandList(discarded.Finish());
        }
        using (ICommandContext invalidResolve = device.AcquireCommandContext(QueueType.Graphics))
        {
            Assert.Throws<InvalidOperationException>(() =>
                invalidResolve.ResolveQueryPool(pool, 0, 1, readback, 0));
        }

        using (ICommandContext write = device.AcquireCommandContext(QueueType.Graphics))
        {
            write.WriteTimestamp(pool, 0);
            GpuCompletion completion = device.Submit(QueueType.Graphics, [write.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
        }
        using (ICommandContext resolve = device.AcquireCommandContext(QueueType.Graphics))
        {
            resolve.ResolveQueryPool(pool, 0, 1, readback, 0);
            GpuCompletion completion = device.Submit(QueueType.Graphics, [resolve.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
        }
        byte[] result = new byte[8];
        device.ReadBuffer(readback, 0, result);
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(result) > 0);
        device.DestroyBuffer(readback);
        device.DestroyQueryPool(pool);
        device.CollectGarbage();
        AssertNoNativeErrors(device);
    }

    [Fact]
    public void Warp_timestamp_heap_resolves_monotonic_gpu_ticks()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP timestamp lane must execute on Windows.");
        using Device device = CreateDevice();
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.Timestamp, 2));
        BufferHandle readback = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopyDestination), MemoryType.Readback);

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, "timestamp-query");
        commands.ResetQueryPool(pool, 0, 2);
        commands.WriteTimestamp(pool, 0);
        commands.PushDebugGroup("timestamp-separator");
        commands.PopDebugGroup();
        commands.WriteTimestamp(pool, 1);
        commands.ResolveQueryPool(pool, 0, 2, readback, 0);
        GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] values = new byte[16];
        device.ReadBuffer(readback, 0, values);
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(values);
        ulong second = BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(8));
        Assert.True(first > 0);
        Assert.True(second >= first);
        AssertNoNativeErrors(device);
    }

    [Fact]
    public void Warp_occlusion_query_resolves_visible_sample_count()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP occlusion lane must execute on Windows.");
        using Device device = CreateDevice();
        RasterFixture raster = new(device);
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.Occlusion, 1));
        BufferHandle readback = device.CreateBuffer(new BufferDesc(8, BufferUsage.CopyDestination), MemoryType.Readback);

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, "occlusion-query");
        commands.ResetQueryPool(pool, 0, 1);
        raster.Prepare(commands);
        commands.BeginQuery(pool, 0);
        commands.Draw(3);
        commands.EndQuery(pool, 0);
        raster.End(commands);
        commands.ResolveQueryPool(pool, 0, 1, readback, 0);
        GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        Span<byte> bytes = stackalloc byte[8];
        device.ReadBuffer(readback, 0, bytes);
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(bytes) > 0);
        AssertNoNativeErrors(device);
    }

    [Fact]
    public void Warp_pipeline_statistics_resolve_nonzero_invocations()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP pipeline-statistics lane must execute on Windows.");
        using Device device = CreateDevice();
        RasterFixture raster = new(device);
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.PipelineStatistics, 1));
        BufferHandle readback = device.CreateBuffer(new BufferDesc(
            PipelineStatisticsValues.ByteSize,
            BufferUsage.CopyDestination), MemoryType.Readback);

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, "pipeline-statistics-query");
        commands.ResetQueryPool(pool, 0, 1);
        commands.SetPipeline(raster.Pipeline);
        commands.BeginQuery(pool, 0);
        raster.Prepare(commands, pipelineAlreadySet: true);
        commands.Draw(3);
        raster.End(commands);
        commands.EndQuery(pool, 0);
        commands.ResolveQueryPool(pool, 0, 1, readback, 0);
        GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] bytes = new byte[PipelineStatisticsValues.ByteSize];
        device.ReadBuffer(readback, 0, bytes);
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8)) >= 3);  // IA vertices
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8)) >= 3); // VS invocations
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(56, 8)) > 0);   // PS invocations
        AssertNoNativeErrors(device);
    }

    [Fact]
    public void Warp_uses_queue_timestamp_frequency_and_native_clock_calibration()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP calibration lane must execute on Windows.");
        using Device device = CreateDevice();
        foreach (QueueType queue in new[] { QueueType.Graphics, QueueType.Compute })
        {
            ulong frequency = device.GetTimestampFrequency(queue);
            TimestampCalibration calibration = device.GetTimestampCalibration(queue);
            Assert.Equal(queue, calibration.Queue);
            Assert.Equal(frequency, calibration.TimestampFrequency);
            Assert.True(frequency > 0);
            Assert.True(calibration.CpuTimestamp > 0);
            Assert.True(calibration.GpuTimestamp > 0);
        }
    }

    [Fact]
    public void Warp_query_scope_and_copy_queue_timestamp_misuse_fail_closed()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP query validation lane must execute on Windows.");
        using Device device = CreateDevice();
        QueryPoolHandle timestamps = device.CreateQueryPool(new QueryPoolDesc(QueryType.Timestamp, 1));
        QueryPoolHandle occlusion = device.CreateQueryPool(new QueryPoolDesc(QueryType.Occlusion, 1));

        using ICommandContext copy = device.AcquireCommandContext(QueueType.Copy, "invalid-copy-timestamp");
        Assert.Throws<InvalidOperationException>(() => copy.WriteTimestamp(timestamps, 0));

        using ICommandContext graphics = device.AcquireCommandContext(QueueType.Graphics, "invalid-occlusion-scope");
        Assert.Throws<InvalidOperationException>(() => graphics.BeginQuery(occlusion, 0));
    }

    private static Device CreateDevice() => new(new Options
    {
        UseWarpAdapter = true,
        EnableDebugLayer = true,
        EnableGpuValidation = false,
    });

    private static void AssertNoNativeErrors(Device device) => Assert.DoesNotContain(
        device.DrainDiagnostics(),
        static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

    private sealed class RasterFixture
    {
        private readonly TextureViewHandle _view;

        public RasterFixture(Device device)
        {
            ShaderHandle vertex = device.CreateShader(Shader(
                new ShaderArtifactKey(0x7101, 0x7102, 0x7103, 0x7104),
                ShaderStage.Vertex,
                "VSMain",
                "triangle.vs.dxil"));
            ShaderHandle pixel = device.CreateShader(Shader(
                new ShaderArtifactKey(0x7201, 0x7202, 0x7203, 0x7204),
                ShaderStage.Pixel,
                "PSMain",
                "triangle.ps.dxil"));
            PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                Array.Empty<BindGroupLayoutHandle>(),
                Array.Empty<PushConstantRange>()));
            Pipeline = device.CreateRasterPipeline(new RasterPipelineDesc(
                layout,
                vertex,
                pixel,
                new[] { Format.R8G8B8A8UNorm },
                Rasterizer: new RasterizerDesc(Cull: CullMode.None),
                BlendAttachments: new[]
                {
                    new BlendAttachmentDesc(
                        Enabled: false,
                        SourceColor: BlendFactor.One,
                        DestinationColor: BlendFactor.Zero,
                        ColorOperation: BlendOperation.Add,
                        SourceAlpha: BlendFactor.One,
                        DestinationAlpha: BlendFactor.Zero,
                        AlphaOperation: BlendOperation.Add,
                        WriteMask: ColorWriteMask.All),
                }));
            Texture = device.CreateTexture(new TextureDesc(
                16,
                16,
                Format.R8G8B8A8UNorm,
                TextureUsage.ColorAttachment));
            _view = device.CreateTextureView(new TextureViewDesc(
                Texture,
                TextureSubresourceRange.WholeColor,
                TextureViewUsage.ColorAttachment));
        }

        public PipelineHandle Pipeline { get; }
        public TextureHandle Texture { get; }

        public void Prepare(ICommandContext commands, bool pipelineAlreadySet = false)
        {
            commands.Barriers([ResourceBarrier.Transition(Texture.Resource, ResourceState.Common, ResourceState.RenderTarget)]);
            if (!pipelineAlreadySet) commands.SetPipeline(Pipeline);
            commands.SetViewport(new Viewport(0, 0, 16, 16));
            commands.SetScissor(new Rect(0, 0, 16, 16));
            commands.BeginRendering(new RenderingInfo(
                new[] { new ColorAttachment(_view, LoadAction.Clear, StoreAction.Store) },
                null,
                16,
                16));
        }

        public void End(ICommandContext commands) => commands.EndRendering();

        private static ShaderDesc Shader(
            ShaderArtifactKey key,
            ShaderStage stage,
            string entryPoint,
            string fixture) => new(
                key,
                ShaderBinaryFormat.Dxil,
                stage,
                entryPoint,
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)),
                new ShaderInterface(Array.Empty<ShaderBinding>(), Array.Empty<PushConstantRange>(), 1),
                $"test:{entryPoint}");
    }
}
