using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpCapabilityTests
{
    [Fact]
    public void Graphics_pipeline_accepts_combined_known_dynamic_states_and_rejects_unknown_bits()
    {
        const string source = """
            struct VertexOutput { float4 Position : SV_Position; };
            [shader("vertex")]
            VertexOutput vertexMain(uint id : SV_VertexID)
            {
                VertexOutput value;
                value.Position = float4(0, 0, 0, 1);
                return value;
            }
            [shader("fragment")]
            float4 pixelMain() : SV_Target0 { return float4(1, 1, 1, 1); }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("vertexMain", SlangStage.Vertex),
            new("pixelMain", SlangStage.Fragment),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_dynamic_state_flags",
            source,
            entries);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Format[] formats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blendAttachments =
        [
            new(Enabled: false, WriteMask: ColorWriteMasks.All),
        ];
        BlendState blend = new(blendAttachments);
        AttachmentFormatSignature attachments = new(formats, null);

        using Pipeline pipeline = backend.CreateGraphicsPipeline(
            device,
            new GraphicsPipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                shader.GetEntryPoint(1),
                [],
                [],
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                new RasterizerState(Cull: CullType.None),
                new MultisampleState(SampleCount: 1),
                new DepthStencilState(),
                blend,
                attachments,
                DynamicStates.Viewport | DynamicStates.Scissor));

        try
        {
            using Pipeline invalid = backend.CreateGraphicsPipeline(
                device,
                new GraphicsPipelineDesc(
                    shader.Program,
                    shader.GetEntryPoint(0),
                    shader.GetEntryPoint(1),
                    [],
                    [],
                    PrimitiveTopology.TriangleList,
                    StripCut.Disabled,
                    new RasterizerState(Cull: CullType.None),
                    new MultisampleState(SampleCount: 1),
                    new DepthStencilState(),
                    blend,
                    attachments,
                    (DynamicStates)0x8000));
            Assert.Fail("Unknown DynamicStates bits must be rejected.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    [Fact]
    public void Timestamp_queries_execute_on_every_queue_family_and_reject_cross_family_use()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        foreach (QueueType type in Enum.GetValues<QueueType>())
        {
            using QueryPool pool = backend.CreateQueryPool(
                device,
                new QueryPoolDesc(QueryType.Timestamp, type, 1));
            using Buffer destination = backend.CreateBuffer(
                device,
                new BufferDesc(pool.ResultInfo.ResultStride, BufferUsages.QueryResolve),
                MemoryType.Readback);
            using CommandContext context = backend.CreateCommandContext(
                device,
                new CommandContextDesc(type, 0, 1));

            backend.Begin(context);
            backend.WriteTimestamp(context, pool, 0);
            backend.ResolveQueries(
                context,
                pool,
                0,
                1,
                destination,
                new BufferRange(0, pool.ResultInfo.ResultStride));
            using RecordedCommands commands = backend.End(context);
            RecordedCommands[] submitted = [commands];
            QueueCompletion completion = backend.Submit(
                backend.GetQueue(device, type),
                new QueueSubmitDesc([], [], submitted, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }

        using QueryPool graphicsPool = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
        using CommandContext computeContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(computeContext);
        Assert.Throws<InvalidOperationException>(() =>
            backend.WriteTimestamp(computeContext, graphicsPool, 0));
        backend.Discard(computeContext);
    }

    [Fact]
    public void Query_pool_creation_rejects_invalid_queue_and_stream_combinations()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.Throws<ArgumentException>(() => backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Occlusion, QueueType.Compute, 1)));
        Assert.Throws<ArgumentException>(() => backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1, StreamIndex: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateQueryPool(
            device,
            new QueryPoolDesc(
                QueryType.StreamOutputStatistics,
                QueueType.Graphics,
                1,
                StreamIndex: 4)));
    }

    [Fact]
    public void Snapshot_exposes_only_usable_WARP_capabilities()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        AssertCapability<SparseResources>(backend, device, DeviceFeatures.SparseResources);
        AssertCapability<Residency>(backend, device, DeviceFeatures.Residency);
        AssertCapability<RayTracing>(backend, device, DeviceFeatures.RayTracing);
        AssertCapability<MeshShaders>(backend, device, DeviceFeatures.MeshShaders);
        AssertCapability<VariableRateShading>(backend, device, DeviceFeatures.VariableRateShading);
        AssertCapability<WorkGraphs>(backend, device, DeviceFeatures.WorkGraphs);
        AssertCapability<IndirectCommands>(backend, device, DeviceFeatures.IndirectCommands);
        AssertCapability<CalibratedTimestamps>(backend, device, DeviceFeatures.CalibratedTimestamps);
        AssertCapability<ExternalResources>(backend, device, DeviceFeatures.ExternalResources);
        AssertCapability<ExternalTimelines>(backend, device, DeviceFeatures.ExternalTimelines);

        Assert.True(backend.TryGetCapability(device, out SparseResources? sparse));
        Assert.NotNull(sparse);
        Assert.Equal(sparse.Texture2DSupported, !sparse.SupportedTexture2DFormats.IsEmpty);
        Assert.Equal(sparse.Texture3DSupported, !sparse.SupportedTexture3DFormats.IsEmpty);
        Assert.True(sparse.SupportedTexture2DFormats.Contains(Format.R8G8B8A8UNorm));

        Assert.True(backend.TryGetCapability(device, out RayTracing? rays));
        Assert.NotNull(rays);
        Assert.True(rays.PipelineRayTracing);
        Assert.Equal(16_777_216U, rays.MaximumGeometriesPerBottomLevel);
        Assert.Equal(16_777_216U, rays.MaximumInstancesPerTopLevel);
        Assert.Equal(536_870_912U, rays.MaximumPrimitivesPerBottomLevel);
        Assert.Equal(1_073_741_824U, rays.MaximumRayGenerationShaderThreads);
        Assert.Equal(4_096U, rays.MaximumShaderRecordStride);

        Assert.True(backend.TryGetCapability(device, out MeshShaders? mesh));
        Assert.NotNull(mesh);
        Assert.Equal(4_194_303U, mesh.MaximumTotalThreadGroupCount);

        Assert.True(backend.TryGetCapability(device, out IndirectCommands? indirect));
        Assert.NotNull(indirect);
        Assert.Equal(0xFFFF_FFFCU, indirect.MaximumStride);

        Format[] allFormats = Enum.GetValues<Format>();
        Assert.Equal(allFormats.Length, device.Capabilities.Formats.Length);
        for (int index = 0; index < allFormats.Length; index++)
            Assert.Equal(allFormats[index], device.Capabilities.Formats[index].Format);
        FormatSupport color = device.Capabilities.GetFormatSupport(Format.R8G8B8A8UNorm);
        Assert.Equal(
            FormatFeatures.Texture2D |
            FormatFeatures.ShaderLoad |
            FormatFeatures.ShaderSample |
            FormatFeatures.ColorAttachment,
            color.Features &
            (FormatFeatures.Texture2D |
             FormatFeatures.ShaderLoad |
             FormatFeatures.ShaderSample |
             FormatFeatures.ColorAttachment));
        Assert.True(color.SupportsSampleCount(1));
        Assert.False(color.SupportsSampleCount(3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.Capabilities.GetFormatSupport((Format)0));

        foreach (Format format in sparse.SupportedTexture2DFormats)
        {
            FormatSupport support = device.Capabilities.GetFormatSupport(format);
            Assert.True((support.Features & FormatFeatures.SparseTexture2D) != 0);
            Assert.True(support.SupportsSparseSampleCount(1));
        }

        bool samplerFeedbackAvailable = backend.TryGetCapability(
            device,
            out SamplerFeedback? feedback);
        Assert.Equal(samplerFeedbackAvailable, feedback is not null);
        Assert.Equal(
            samplerFeedbackAvailable,
            (device.Capabilities.Features & DeviceFeatures.SamplerFeedback) != 0);
        if (feedback is not null)
        {
            Assert.True(Enum.IsDefined(feedback.Tier));
            Assert.NotEmpty(feedback.SupportedFormats.ToArray());
            Assert.True(feedback.MinimumMipRegionWidth > 0);
            Assert.True(feedback.MinimumMipRegionHeight > 0);
            Assert.True(feedback.FeedbackMapAlignment > 0);
            HardwareSamplerFeedbackTests.AssertClearDecodeAndParentCascade(backend, device);
        }
        else
        {
            using Texture sampled = backend.CreateTexture(
                device,
                new TextureDesc(
                    TextureDimension.Texture2D,
                    16,
                    16,
                    1,
                    1,
                    1,
                    1,
                    Format.R8G8B8A8UNorm,
                    TextureUsages.Sampled));
            Assert.Throws<NotSupportedException>(() =>
                backend.CreateSamplerFeedbackTexture(
                    device,
                    new SamplerFeedbackTextureDesc(
                        sampled,
                        SamplerFeedbackType.MinimumMip,
                        4,
                        4)));
        }
        Assert.False(backend.TryGetCapability(device, out LinkedAdapters? linked));
        Assert.Null(linked);
        Assert.Equal(DeviceFeatures.None, device.Capabilities.Features & DeviceFeatures.LinkedAdapters);
    }

    [Fact]
    public void Calibrated_timestamps_sample_the_exact_locked_queue()
    {
        using D3D12Backend backend = new();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        foreach (QueueType type in Enum.GetValues<QueueType>())
        {
            CalibratedTimestampInfo sample = backend.CalibrateTimestamps(
                backend.GetQueue(device, type));
            Assert.True(sample.CpuCounter > 0);
            Assert.True(sample.CpuFrequency > 0);
            Assert.True(sample.QueueFrequency > 0);
        }

        Queue queue = backend.GetQueue(device, QueueType.Compute);
        D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim finished = new();
        Exception? failure = null;
        CalibratedTimestampInfo result = default;
        Thread calibration = new(() =>
        {
            started.Set();
            try
            {
                result = backend.CalibrateTimestamps(queue);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                finished.Set();
            }
        });
        calibration.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.False(finished.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            held.Dispose();
        }

        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        calibration.Join();
        Assert.Null(failure);
        Assert.True(result.CpuFrequency > 0);
        Assert.True(result.QueueFrequency > 0);
    }

    [Fact]
    public void Residency_make_resident_completion_precedes_explicit_evict()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out Residency? capability));
        Assert.NotNull(capability);

        ResidencyInfo info = backend.GetResidencyInfo(device);
        Assert.True(info.LocalBudget > 0 || info.NonLocalBudget > 0);

        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(64 * 1024, BufferUsages.CopySource | BufferUsages.CopyDestination));
        ResidencyResource resource = backend.GetResidencyResource(buffer);
        Assert.False(resource.IsDefault);
        Assert.Same(device, resource.Device);
        Queue queue = backend.GetQueue(device, QueueType.Copy);

        QueueCompletion resident = backend.EnqueueMakeResident(queue, [resource]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(resident, TimeSpan.FromSeconds(10)));
        backend.Evict(device, [resource]);
        QueueCompletion restored = backend.EnqueueMakeResident(queue, [resource]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(restored, TimeSpan.FromSeconds(10)));

        Assert.Throws<ArgumentException>(() =>
            backend.EnqueueMakeResident(queue, [default(ResidencyResource)]));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Sparse_map_copy_use_and_unmap_follow_queue_order()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? capability));
        Assert.NotNull(capability);
        Assert.True(capability.BufferSupported);

        ulong tileSize = capability.TileSizeInBytes;
        using Buffer source = backend.CreateReservedBuffer(
            device,
            new BufferDesc(tileSize, BufferUsages.CopySource | BufferUsages.CopyDestination));
        using Buffer destination = backend.CreateReservedBuffer(
            device,
            new BufferDesc(tileSize, BufferUsages.CopySource | BufferUsages.CopyDestination));
        SparseResourceInfo sourceInfo = backend.GetSparseResourceInfo(source);
        SparseResourceInfo destinationInfo = backend.GetSparseResourceInfo(destination);
        Assert.Equal(tileSize, sourceInfo.Alignment);
        Assert.Equal(tileSize, destinationInfo.Alignment);
        Assert.True(sourceInfo.TotalTileCount >= 1);
        Assert.True(destinationInfo.TotalTileCount >= 1);

        using Heap heap = backend.CreateHeap(
            device,
            new HeapDesc(tileSize, tileSize, MemoryType.DeviceLocal, HeapFlags.Buffers));
        SparseTileCoordinate origin = new(0, 0, 0, 0);
        SparseTileRegion oneTile = new(origin, 0, 0, 0, 1, Boxed: false);
        Queue queue = backend.GetQueue(device, QueueType.Copy);

        QueueCompletion mapped = backend.UpdateSparseMappings(
            queue,
            [new SparseMappingDesc(source, oneTile, SparseMappingType.Mapped, heap, 0)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(mapped, TimeSpan.FromSeconds(10)));
        QueueCompletion copied = backend.CopySparseMappings(
            queue,
            [new SparseMappingCopyDesc(destination, origin, source, origin, oneTile)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(copied, TimeSpan.FromSeconds(10)));

        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(tileSize, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(tileSize, BufferUsages.CopyDestination),
            MemoryType.Readback);
        BufferRange full = new(0, tileSize);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, full))
        {
            for (int index = 0; index < mapping.Bytes.Length; index++)
                mapping.Bytes[index] = unchecked((byte)(index * 29 + 7));
            mapping.Flush(full);
        }

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            source,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination));
        backend.CopyBuffer(context, new BufferCopy(upload, 0, source, 0, tileSize));
        AliasingResource[] before = [new(source)];
        AliasingResource[] after = [new(destination)];
        backend.Barrier(context, new AliasingBarrier(before, after));
        backend.Barrier(context, new BufferBarrier(
            destination,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(destination, 0, readback, 0, tileSize));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion executed = Submit(backend, queue, commands);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(executed, TimeSpan.FromSeconds(10)));

        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, full))
        {
            mapping.Invalidate(full);
            for (int index = 0; index < mapping.Bytes.Length; index++)
                Assert.Equal(unchecked((byte)(index * 29 + 7)), mapping.Bytes[index]);
        }

        SparseMappingDesc[] unmaps =
        [
            new(source, oneTile, SparseMappingType.Unmapped, null, 0),
            new(destination, oneTile, SparseMappingType.Unmapped, null, 0),
        ];
        QueueCompletion unmapped = backend.UpdateSparseMappings(queue, unmaps);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(unmapped, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Indirect_command_snapshot_accepts_every_advertised_action_family()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out IndirectCommands? capability));
        Assert.NotNull(capability);

        AssertLayout(IndirectArgumentType.Draw, 16, IndirectArgumentTypes.Draw);
        AssertLayout(IndirectArgumentType.DrawIndexed, 20, IndirectArgumentTypes.DrawIndexed);
        AssertLayout(IndirectArgumentType.Dispatch, 12, IndirectArgumentTypes.Dispatch);
        AssertLayout(IndirectArgumentType.DispatchMesh, 12, IndirectArgumentTypes.DispatchMesh);
        AssertLayout(IndirectArgumentType.DispatchRays, 104, IndirectArgumentTypes.DispatchRays);
        AssertLayout(IndirectArgumentType.Dispatch, 4_096, IndirectArgumentTypes.Dispatch);

        IndirectArgumentDesc[] vertexDraw =
        [
            new(IndirectArgumentType.VertexBuffer, Slot: 0),
            new(IndirectArgumentType.Draw),
        ];
        using IndirectCommandLayout vertexLayout = backend.CreateIndirectCommandLayout(
            device,
            new IndirectCommandLayoutDesc(vertexDraw, 32));
        IndirectArgumentDesc[] indexDraw =
        [
            new(IndirectArgumentType.IndexBuffer),
            new(IndirectArgumentType.DrawIndexed),
        ];
        using IndirectCommandLayout indexLayout = backend.CreateIndirectCommandLayout(
            device,
            new IndirectCommandLayoutDesc(indexDraw, 36));

        Assert.Throws<ArgumentException>(() =>
            CreateRootArgumentLayout(backend, device));
        Assert.Equal(
            IndirectArgumentTypes.None,
            capability.ArgumentTypes & IndirectArgumentTypes.WorkGraph);
        Assert.Throws<NotSupportedException>(() =>
            CreateWorkGraphLayout(backend, device));

        void AssertLayout(
            IndirectArgumentType type,
            uint stride,
            IndirectArgumentTypes expected)
        {
            Assert.Equal(expected, capability.ArgumentTypes & expected);
            IndirectArgumentDesc[] arguments = [new(type)];
            using IndirectCommandLayout layout = backend.CreateIndirectCommandLayout(
                device,
                new IndirectCommandLayoutDesc(arguments, stride));
            Assert.Equal(stride, layout.Stride);
        }
    }

    [Fact]
    public void Execute_indirect_dispatch_runs_a_linked_Slang_compute_pipeline()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 dispatchThread : SV_DispatchThreadID)
            {
            }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_indirect_compute",
            source,
            entries);
        using IGraphicsBackend backend = CreateValidatedBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        IndirectArgumentDesc[] arguments = [new(IndirectArgumentType.Dispatch)];
        using IndirectCommandLayout layout = backend.CreateIndirectCommandLayout(
            device,
            new IndirectCommandLayoutDesc(arguments, 12));
        using Buffer argumentBuffer = backend.CreateBuffer(
            device,
            new BufferDesc(12, BufferUsages.Indirect),
            MemoryType.Upload);
        BufferRange range = new(0, 12);
        using (MappedBuffer mapping = backend.Map(argumentBuffer, MapType.Write, range))
        {
            BitConverter.TryWriteBytes(mapping.Bytes[0..4], 1u);
            BitConverter.TryWriteBytes(mapping.Bytes[4..8], 1u);
            BitConverter.TryWriteBytes(mapping.Bytes[8..12], 1u);
            mapping.Flush(range);
        }

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.ExecuteIndirect(
            context,
            layout,
            new BufferRegion(argumentBuffer, range),
            1);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(
            backend,
            backend.GetQueue(device, QueueType.Compute),
            commands);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Variable_rate_shading_records_reported_rates_and_image_tier()
    {
        using IGraphicsBackend backend = CreateValidatedBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out VariableRateShading? capability));
        Assert.NotNull(capability);
        Assert.True(capability.Rates.Contains(ShadingRate.Rate1x1));
        Assert.True(capability.Rates.Contains(ShadingRate.Rate2x2));
        Assert.True(capability.Combiners.Contains(ShadingRateCombiner.Passthrough));

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Texture? image = null;
        try
        {
            if (capability.ShadingRateImage)
            {
                Assert.True(capability.ImageTileWidth > 0);
                Assert.True(capability.ImageTileHeight > 0);
                image = backend.CreateTexture(
                    device,
                    new TextureDesc(
                        TextureDimension.Texture2D,
                        capability.ImageTileWidth,
                        capability.ImageTileHeight,
                        1,
                        1,
                        1,
                        1,
                        Format.R8UInt,
                        TextureUsages.ShadingRate));
            }

            backend.Begin(context);
            foreach (ShadingRate rate in capability.Rates)
            {
                backend.SetShadingRate(
                    context,
                    rate,
                    ShadingRateCombiner.Passthrough,
                    ShadingRateCombiner.Passthrough);
            }
            if (image is not null)
            {
                backend.Barrier(context, new TextureBarrier(
                    image,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.None,
                    PipelineSync.Draw,
                    ResourceAccess.NoAccess,
                    ResourceAccess.ShadingRateSource,
                    TextureLayout.Undefined,
                    TextureLayout.ShadingRateSource));
                backend.SetShadingRateImage(context, image);
                backend.SetShadingRateImage(context, null);
            }
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = Submit(
                backend,
                backend.GetQueue(device, QueueType.Graphics),
                commands);
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            image?.Dispose();
        }
    }

    [Fact]
    public void Mesh_pipeline_and_direct_indirect_dispatch_execute_on_WARP()
    {
        const string source = """
            struct MeshVertex
            {
                float4 Position : SV_Position;
            };

            [shader("mesh")]
            [outputtopology("triangle")]
            [numthreads(1, 1, 1)]
            void meshMain(
                out vertices MeshVertex outputVertices[3],
                out indices uint3 outputTriangles[1])
            {
                SetMeshOutputCounts(3, 1);
                outputVertices[0].Position = float4(-1.0, -1.0, 0.0, 1.0);
                outputVertices[1].Position = float4(0.0, 1.0, 0.0, 1.0);
                outputVertices[2].Position = float4(1.0, -1.0, 0.0, 1.0);
                outputTriangles[0] = uint3(0, 1, 2);
            }

            [shader("pixel")]
            float4 pixelMain(float4 position : SV_Position) : SV_Target0
            {
                return float4(0.25, 0.5, 0.75, 1.0);
            }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("meshMain", SlangStage.Mesh),
            new("pixelMain", SlangStage.Fragment),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileHlslPassThrough(
            "rhi_mesh_dispatch",
            source,
            entries);
        using IGraphicsBackend backend = CreateValidatedBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out MeshShaders? capability));
        Assert.NotNull(capability);
        Assert.Equal(4_194_303U, capability.MaximumTotalThreadGroupCount);

        BlendAttachmentState[] blendAttachments =
        [
            new(WriteMask: ColorWriteMasks.All),
        ];
        BlendState blend = new(blendAttachments);
        Format[] colorFormats = [Format.R8G8B8A8UNorm];
        AttachmentFormatSignature attachments = new(colorFormats, null);
        RasterizerState rasterizer = new(Cull: CullType.None);
        MultisampleState multisample = new(SampleCount: 1);
        DepthStencilState depthStencil = new();
        using Pipeline pipeline = backend.CreateMeshPipeline(
            device,
            new MeshPipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                EntryPointReflection.Null,
                shader.GetEntryPoint(1),
                rasterizer,
                multisample,
                depthStencil,
                blend,
                attachments));

        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                8,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));
        TextureSubresourceRange targetRange = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView targetView = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                target,
                targetRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));

        using Buffer arguments = backend.CreateBuffer(
            device,
            new BufferDesc(12, BufferUsages.Indirect),
            MemoryType.Upload);
        BufferRange argumentRange = new(0, 12);
        using (MappedBuffer mapping = backend.Map(arguments, MapType.Write, argumentRange))
        {
            BitConverter.TryWriteBytes(mapping.Bytes[0..4], 1u);
            BitConverter.TryWriteBytes(mapping.Bytes[4..8], 1u);
            BitConverter.TryWriteBytes(mapping.Bytes[8..12], 1u);
            mapping.Flush(argumentRange);
        }
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        ColorAttachmentDesc[] colors =
        [
            new(
                targetView,
                LoadType.Clear,
                StoreType.Store,
                new System.Numerics.Vector4(0, 0, 0, 1)),
        ];
        Viewport[] viewports = [new(0, 0, 8, 8)];
        ScissorRect[] scissors = [new(0, 0, 8, 8)];

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            target,
            targetRange,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, viewports);
        backend.SetScissors(context, scissors);
        backend.BeginRendering(context, new RenderingDesc(colors, null, 8, 8));
        Assert.Throws<InvalidOperationException>(() =>
            DispatchMeshPastLimit(backend, context, capability));
        Assert.Throws<InvalidOperationException>(() =>
            DispatchMeshPastTotalLimit(backend, context, capability));
        backend.DispatchMesh(context, new DispatchArguments(1, 1, 1));
        backend.DispatchMeshIndirect(
            context,
            new BufferRegion(arguments, argumentRange));
        backend.EndRendering(context);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(
            backend,
            backend.GetQueue(device, QueueType.Graphics),
            commands);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    private static ValidationLayer<D3D12Backend> CreateValidatedBackend()
    {
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        return new ValidationLayer<D3D12Backend>(
            new D3D12Backend(new D3D12BackendOptions(validation)));
    }

    private static void AssertCapability<TCapability>(
        IGraphicsBackend backend,
        Device device,
        DeviceFeatures feature)
        where TCapability : DeviceCapability
    {
        Assert.Equal(feature, device.Capabilities.Features & feature);
        Assert.True(backend.TryGetCapability(device, out TCapability? capability));
        Assert.NotNull(capability);
        Assert.Same(device, capability.Device);
    }

    private static QueueCompletion Submit(
        IGraphicsBackend backend,
        Queue queue,
        RecordedCommands commands)
    {
        RecordedCommands[] batch = [commands];
        return backend.Submit(queue, new QueueSubmitDesc([], [], batch, [], []));
    }

    private static void CreateRootArgumentLayout(IGraphicsBackend backend, Device device)
    {
        IndirectArgumentDesc[] arguments =
        [
            new(IndirectArgumentType.Constants, Slot: 0, ValueCount: 1),
            new(IndirectArgumentType.Dispatch),
        ];
        using IndirectCommandLayout _ = backend.CreateIndirectCommandLayout(
            device,
            new IndirectCommandLayoutDesc(arguments, 16));
    }

    private static void CreateWorkGraphLayout(IGraphicsBackend backend, Device device)
    {
        IndirectArgumentDesc[] arguments = [new(IndirectArgumentType.WorkGraph)];
        using IndirectCommandLayout _ = backend.CreateIndirectCommandLayout(
            device,
            new IndirectCommandLayoutDesc(arguments, 16));
    }

    private static void DispatchMeshPastLimit(
        IGraphicsBackend backend,
        CommandContext context,
        MeshShaders capability) =>
        backend.DispatchMesh(
            context,
            new DispatchArguments(checked(capability.MaximumThreadGroupCountX + 1), 1, 1));

    private static void DispatchMeshPastTotalLimit(
        IGraphicsBackend backend,
        CommandContext context,
        MeshShaders capability) =>
        backend.DispatchMesh(
            context,
            new DispatchArguments(
                capability.MaximumThreadGroupCountX,
                checked(capability.MaximumTotalThreadGroupCount /
                    capability.MaximumThreadGroupCountX + 1),
                1));
}
