using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpReceiverTests
{
    [Fact]
    public void Warp_copy_round_trips_upload_and_readback_memory()
    {
        Assert.True(OperatingSystem.IsWindows());
        using IGraphicsBackend backend = new D3D12Backend();
        byte[] source = CreatePattern(1024);

        byte[] result = D3D12TestSupport.ExecuteCopyChain(backend, source);

        Assert.Equal(source, result);
    }

    [Fact]
    public void Receiver_rejects_objects_created_by_another_backend_instance()
    {
        using var receiver = new D3D12Backend();
        using var foreignReceiver = new D3D12Backend();
        using Device foreignDevice = D3D12TestSupport.CreateWarpDevice(foreignReceiver);
        using Buffer foreignBuffer = foreignReceiver.CreateBuffer(
            foreignDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using CommandContext foreignContext = foreignReceiver.CreateCommandContext(
            foreignDevice,
            new CommandContextDesc(QueueType.Copy, 0, 1));

        _ = Assert.Throws<ArgumentException>(() =>
            receiver.GetQueue(foreignDevice, QueueType.Copy));
        _ = Assert.Throws<ArgumentException>(() =>
            receiver.Map(foreignBuffer, MapType.Write, new BufferRange(0, 64)));
        _ = Assert.Throws<ArgumentException>(() => receiver.Begin(foreignContext));
    }

    [Fact]
    public void Concrete_and_interface_receiver_chains_produce_identical_native_results()
    {
        Assert.True(OperatingSystem.IsWindows());
        byte[] source = CreatePattern(257);

        byte[] concreteResult;
        using (var backend = new D3D12Backend())
            concreteResult = ExecuteConcrete(backend, source);

        byte[] interfaceResult;
        using (IGraphicsBackend backend = new D3D12Backend())
            interfaceResult = ExecuteInterface(backend, source);

        Assert.Equal(source, concreteResult);
        Assert.Equal(concreteResult, interfaceResult);
    }

    [Fact]
    public void Stable_empty_submit_allocates_no_managed_memory()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        QueueSubmitDesc submit = new([], [], [], [], []);

        QueueCompletion warmup = backend.Submit(queue, submit);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));

        long before = GC.GetAllocatedBytesForCurrentThread();
        QueueCompletion measured = backend.Submit(queue, submit);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Stable_nonempty_submit_allocates_no_managed_memory()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 256));
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 256));
        using RecordedCommands recorded = backend.End(context);
        commands[0] = recorded;
        QueueSubmitDesc submit = new([], [], commands, [], []);

        long before = GC.GetAllocatedBytesForCurrentThread();
        QueueCompletion measured = backend.Submit(queue, submit);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Stable_copy_frame_allocates_no_managed_memory_between_begin_and_submit()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 256));
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        RecordedCommands recorded = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 256));
        recorded = backend.End(context);
        commands[0] = recorded;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorded.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Stable_rendering_frame_allocates_no_managed_memory_between_begin_and_submit()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView view = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                target,
                range,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        var colors = new[]
        {
            new ColorAttachmentDesc(
                view,
                LoadType.Clear,
                StoreType.Store,
                new Vector4(0.25f, 0.5f, 0.75f, 1)),
        };
        var viewports = new[] { new Viewport(0, 0, 64, 64) };
        var scissors = new[] { new ScissorRect(0, 0, 64, 64) };
        var commands = new RecordedCommands[1];

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            target,
            range,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.SetViewports(context, viewports);
        backend.SetScissors(context, scissors);
        backend.BeginRendering(context, new RenderingDesc(colors, null, 64, 64));
        backend.EndRendering(context);
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        RecordedCommands recorded = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.SetViewports(context, viewports);
        backend.SetScissors(context, scissors);
        backend.BeginRendering(context, new RenderingDesc(colors, null, 64, 64));
        backend.EndRendering(context);
        recorded = backend.End(context);
        commands[0] = recorded;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorded.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Stable_clear_buffer_uses_retained_upload_storage_without_managed_allocation()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(1024, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];
        const uint pattern = 0xA1B2C3D4;

        backend.Begin(context);
        backend.ClearBuffer(context, destination, BufferRange.Whole, pattern);
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        RecordedCommands recorded = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.ClearBuffer(context, destination, BufferRange.Whole, pattern);
        recorded = backend.End(context);
        commands[0] = recorded;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorded.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
        BufferRange readRange = new(0, 1024);
        using MappedBuffer mapping = backend.Map(destination, MapType.Read, readRange);
        mapping.Invalidate(readRange);
        for (int offset = 0; offset < mapping.Bytes.Length; offset += sizeof(uint))
            Assert.Equal(pattern, BitConverter.ToUInt32(mapping.Bytes.Slice(offset, sizeof(uint))));
    }

    [Fact]
    public void Stable_clear_texture_reuses_command_slot_attachment_descriptors()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        var commands = new RecordedCommands[1];
        Vector4 clear = new(0.125f, 0.25f, 0.5f, 1);

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            target,
            range,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.ClearTexture(context, target, range, clear);
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        RecordedCommands recorded = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.ClearTexture(context, target, range, clear);
        recorded = backend.End(context);
        commands[0] = recorded;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorded.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Repeated_normalized_state_calls_produce_valid_direct_and_bundle_payloads()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out VariableRateShading? shadingRate));
        Assert.NotNull(shadingRate);
        using Buffer stateBuffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.Vertex |
                BufferUsages.Index |
                BufferUsages.StreamOutput |
                BufferUsages.Predication),
            MemoryType.DeviceLocal);
        using Texture? shadingRateImage = shadingRate!.ShadingRateImage
            ? backend.CreateTexture(
                device,
                new TextureDesc(
                    TextureDimension.Texture2D,
                    shadingRate.ImageTileWidth,
                    shadingRate.ImageTileHeight,
                    1,
                    1,
                    1,
                    1,
                    Format.R8UInt,
                    TextureUsages.ShadingRate))
            : null;
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        float firstNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC0_0001));
        float secondNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC0_1234));
        Viewport[] firstViewport = [new(+0.0f, +0.0f, 64, 64, -0.0f, 1)];
        Viewport[] equivalentViewport = [new(-0.0f, -0.0f, 64, 64, +0.0f, 1)];
        ScissorRect[] scissors = [new(0, 0, 64, 64)];
        VertexBufferBinding[] vertexBuffers = [new(stateBuffer, 0, 16, 64)];
        IndexBufferBinding indexBuffer = new(stateBuffer, 0, 64, IndexType.UInt16);
        StreamOutputBufferBinding[] streamOutputBuffers = [new(stateBuffer, 0, 64)];
        Vector4 firstBlend = new(firstNaN, +0.0f, -0.0f, 1);
        Vector4 secondBlend = new(secondNaN, -0.0f, +0.0f, 1);

        backend.Begin(context);
        backend.SetVertexBuffers(context, 0, vertexBuffers);
        backend.SetVertexBuffers(context, 0, vertexBuffers.ToArray());
        backend.SetIndexBuffer(context, indexBuffer);
        backend.SetIndexBuffer(context, indexBuffer);
        backend.SetStreamOutputBuffers(context, 0, streamOutputBuffers);
        backend.SetStreamOutputBuffers(context, 0, streamOutputBuffers.ToArray());
        backend.SetViewports(context, firstViewport);
        backend.SetViewports(context, equivalentViewport);
        backend.SetScissors(context, scissors);
        backend.SetScissors(context, scissors.ToArray());
        backend.SetBlendConstants(context, firstBlend);
        backend.SetBlendConstants(context, secondBlend);
        backend.SetStencilReference(context, 7);
        backend.SetStencilReference(context, 7);
        backend.SetDepthBounds(context, +0.0f, 1);
        backend.SetDepthBounds(context, -0.0f, 1);
        backend.SetDepthBias(context, 2, +0.0f, -0.0f);
        backend.SetDepthBias(context, 2, -0.0f, +0.0f);
        backend.SetPrimitiveTopology(context, PrimitiveTopology.TriangleList);
        backend.SetPrimitiveTopology(context, PrimitiveTopology.TriangleList);
        backend.SetStripCut(context, StripCut.Disabled);
        backend.SetStripCut(context, StripCut.Disabled);
        backend.SetPredication(context, stateBuffer, 0, PredicationOperation.NotEqualZero);
        backend.SetPredication(context, stateBuffer, 0, PredicationOperation.NotEqualZero);
        backend.SetShadingRate(
            context,
            ShadingRate.Rate1x1,
            ShadingRateCombiner.Passthrough,
            ShadingRateCombiner.Passthrough);
        backend.SetShadingRate(
            context,
            ShadingRate.Rate1x1,
            ShadingRateCombiner.Passthrough,
            ShadingRateCombiner.Passthrough);
        if (shadingRateImage is not null)
        {
            backend.SetShadingRateImage(context, shadingRateImage);
            backend.SetShadingRateImage(context, shadingRateImage);
        }
        using RecordedCommands commands = backend.End(context);

        using CommandContext bundleContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1, Bundle: true));
        backend.Begin(bundleContext);
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetStreamOutputBuffers(bundleContext, 0, streamOutputBuffers));
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetViewports(bundleContext, firstViewport));
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetScissors(bundleContext, scissors));
        backend.SetVertexBuffers(bundleContext, 0, vertexBuffers);
        backend.SetVertexBuffers(bundleContext, 0, vertexBuffers.ToArray());
        backend.SetIndexBuffer(bundleContext, indexBuffer);
        backend.SetIndexBuffer(bundleContext, indexBuffer);
        backend.SetBlendConstants(bundleContext, firstBlend);
        backend.SetBlendConstants(bundleContext, secondBlend);
        backend.SetStencilReference(bundleContext, 7);
        backend.SetStencilReference(bundleContext, 7);
        backend.SetDepthBounds(bundleContext, +0.0f, 1);
        backend.SetDepthBounds(bundleContext, -0.0f, 1);
        backend.SetDepthBias(bundleContext, 2, +0.0f, -0.0f);
        backend.SetDepthBias(bundleContext, 2, -0.0f, +0.0f);
        backend.SetPrimitiveTopology(bundleContext, PrimitiveTopology.TriangleList);
        backend.SetPrimitiveTopology(bundleContext, PrimitiveTopology.TriangleList);
        backend.SetStripCut(bundleContext, StripCut.Disabled);
        backend.SetStripCut(bundleContext, StripCut.Disabled);
        backend.SetShadingRate(
            bundleContext,
            ShadingRate.Rate1x1,
            ShadingRateCombiner.Passthrough,
            ShadingRateCombiner.Passthrough);
        if (shadingRateImage is not null)
        {
            backend.SetShadingRateImage(bundleContext, shadingRateImage);
            backend.SetShadingRateImage(bundleContext, shadingRateImage);
        }
        backend.SetShadingRate(
            bundleContext,
            ShadingRate.Rate1x1,
            ShadingRateCombiner.Passthrough,
            ShadingRateCombiner.Passthrough);
        using RecordedBundle bundle = backend.EndBundle(bundleContext);

    }

    [Fact]
    public void State_shadow_uses_strict_pipeline_and_binding_object_identity()
    {
        const string source = """
            StructuredBuffer<float4> values;
            float multiplier;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                if (values[id.x].x == multiplier) { }
            }

            struct VertexOutput
            {
                float4 position : SV_Position;
            };

            [shader("vertex")]
            VertexOutput vertexMain(uint vertexId : SV_VertexID)
            {
                float2 positions[3] =
                {
                    float2(-1.0, -1.0),
                    float2(0.0, 1.0),
                    float2(1.0, -1.0),
                };
                VertexOutput output;
                output.position = float4(positions[vertexId], 0.0, 1.0);
                return output;
            }

            [shader("fragment")]
            float4 pixelMain(VertexOutput input) : SV_Target0
            {
                return values[0] * multiplier;
            }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("computeMain", SlangStage.Compute),
            new("vertexMain", SlangStage.Vertex),
            new("pixelMain", SlangStage.Fragment),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_state_shadow_content_identity",
            source,
            entries);
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection reflected = GetDataLayout(layout);
        Assert.Equal((nint)1, reflected.BindingRangeCount);

        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);
        BufferSrvDesc ordinaryDescription = new(
            buffer,
            BufferRange.Whole,
            StructureStride: 16,
            Label: "ordinary-wrapper");
        BufferSrvDesc equivalentDescription = ordinaryDescription with
        {
            Label = "equivalent-wrapper",
        };
        using BufferSrv ordinary = backend.CreateBufferSrv(device, ordinaryDescription);
        using BufferSrv equivalent = backend.CreateBufferSrv(device, equivalentDescription);
        ResourceBinding ordinaryBinding = ResourceBinding.ReadOnlyBuffer(ordinary);
        ResourceBinding equivalentBinding = ResourceBinding.ReadOnlyBuffer(equivalent);
        Assert.NotEqual(ordinaryBinding, equivalentBinding);
        Assert.Equal(ordinaryBinding, ResourceBinding.ReadOnlyBuffer(ordinary));

        byte[] ordinaryData = new byte[checked((int)reflected.GetSize(
            SlangParameterCategory.Uniform))];
        ComputePipelineDesc pipelineDescription = new(shader.Program, shader.GetEntryPoint(0));
        using Pipeline firstPipeline = backend.CreateComputePipeline(device, pipelineDescription);
        using Pipeline secondPipeline = backend.CreateComputePipeline(device, pipelineDescription);
        using PersistentParameterBindings firstBindings =
            backend.CreatePersistentParameterBindings(
                device,
                firstPipeline,
                new ParameterBlockBindings(layout, [ordinaryBinding], ordinaryData),
                "first-bindings");
        using PersistentParameterBindings secondBindings =
            backend.CreatePersistentParameterBindings(
                device,
                firstPipeline,
                new ParameterBlockBindings(layout, [equivalentBinding], ordinaryData),
                "second-bindings");
        backend.PublishDescriptors(device);

        Assert.NotSame(firstPipeline, secondPipeline);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.SetPipeline(context, firstPipeline);
        backend.SetPersistentParameterBindings(context, firstBindings);
        backend.SetPersistentParameterBindings(context, secondBindings);
        backend.SetPipeline(context, secondPipeline);
        Assert.Throws<ArgumentException>(() =>
            backend.SetPersistentParameterBindings(context, firstBindings));
        Assert.Throws<ArgumentException>(() =>
            backend.SetPersistentParameterBindings(context, secondBindings));
        using RecordedCommands commands = backend.End(context);
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);

        using CommandContext transientContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(transientContext, new CommandRecordingDesc(2, 0, 4));
        backend.SetPipeline(transientContext, firstPipeline);
        backend.SetTransientParameterBindings(
            transientContext,
            new ParameterBlockBindings(layout, [ordinaryBinding], ordinaryData));
        backend.SetTransientParameterBindings(
            transientContext,
            new ParameterBlockBindings(layout, [equivalentBinding], ordinaryData));
        backend.SetPersistentParameterBindings(transientContext, firstBindings);
        backend.SetPersistentParameterBindings(transientContext, secondBindings);
        using RecordedCommands transientCommands = backend.End(transientContext);

        Format[] colorFormats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blendAttachments =
        [
            new(Enabled: false, WriteMask: ColorWriteMasks.All),
        ];
        GraphicsPipelineDesc graphicsDescription = new(
            shader.Program,
            shader.GetEntryPoint(1),
            shader.GetEntryPoint(2),
            [],
            [],
            PrimitiveTopology.TriangleList,
            StripCut.Disabled,
            new RasterizerState(Cull: CullType.None),
            new MultisampleState(SampleCount: 1),
            new DepthStencilState(),
            new BlendState(blendAttachments),
            new AttachmentFormatSignature(colorFormats, null),
            DynamicStates.None);
        using Pipeline firstGraphicsPipeline = backend.CreateGraphicsPipeline(
            device,
            graphicsDescription);
        using Pipeline secondGraphicsPipeline = backend.CreateGraphicsPipeline(
            device,
            graphicsDescription);
        using PersistentParameterBindings firstGraphicsBindings =
            backend.CreatePersistentParameterBindings(
                device,
                firstGraphicsPipeline,
                new ParameterBlockBindings(layout, [ordinaryBinding], ordinaryData),
                "first-graphics-bindings");
        using PersistentParameterBindings secondGraphicsBindings =
            backend.CreatePersistentParameterBindings(
                device,
                firstGraphicsPipeline,
                new ParameterBlockBindings(layout, [equivalentBinding], ordinaryData),
                "second-graphics-bindings");
        backend.PublishDescriptors(device);
        using CommandContext bundleContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1, Bundle: true));
        backend.Begin(bundleContext);
        backend.SetPipeline(bundleContext, firstGraphicsPipeline);
        backend.SetPersistentParameterBindings(bundleContext, firstGraphicsBindings);
        backend.SetPersistentParameterBindings(bundleContext, secondGraphicsBindings);
        backend.SetPipeline(bundleContext, secondGraphicsPipeline);
        Assert.Throws<ArgumentException>(() =>
            backend.SetPersistentParameterBindings(bundleContext, firstGraphicsBindings));
        Assert.Throws<ArgumentException>(() =>
            backend.SetPersistentParameterBindings(bundleContext, secondGraphicsBindings));
        using RecordedBundle bundle = backend.EndBundle(bundleContext);
        Assert.False(bundle.IsDisposed);

    }

    [Fact]
    public void Sixteen_byte_transient_constant_buffer_repeated_values_remain_recordable()
    {
        const string source = """
            float4 tint;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                if (tint.x == id.x) { }
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_transient_constant_buffer_16",
            source,
            [new("computeMain", SlangStage.Compute)]);
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection reflected = GetDataLayout(layout);
        Assert.Equal((nuint)16, reflected.GetSize(SlangParameterCategory.Uniform));
        Assert.Equal((nint)0, reflected.BindingRangeCount);

        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        byte[] first = new byte[16];
        byte[] second = new byte[16];
        first[0] = 1;
        second[0] = 2;
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using PersistentParameterBindings persistent =
            backend.CreatePersistentParameterBindings(
                device,
                pipeline,
                new ParameterBlockBindings(layout, [], first),
                "persistent first value");
        backend.PublishDescriptors(device);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, persistent);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, [], first));
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, [], second));
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, [], second));
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, [], first));
        backend.SetPersistentParameterBindings(context, persistent);
        using RecordedCommands commands = backend.End(context);
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);

    }

    private static byte[] ExecuteConcrete(
        D3D12Backend graphics,
        ReadOnlySpan<byte> source)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = graphics.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(graphics.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        AdapterInfo adapter = Assert.Single(adapters, static value => !value.HardwareAccelerated);
        Assert.False(string.IsNullOrWhiteSpace(adapter.DriverVersion));
        Assert.NotEqual("unavailable", adapter.DriverVersion);
        DeviceQueueDesc[] queues = [new(QueueType.Copy)];

        using Device device = graphics.CreateDevice(new DeviceDesc(
            adapter.Id,
            queues,
            label: "generic receiver proof"));
        using Buffer upload = graphics.CreateBuffer(
            device,
            new BufferDesc(checked((ulong)source.Length), BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = graphics.CreateBuffer(
            device,
            new BufferDesc(checked((ulong)source.Length), BufferUsages.CopyDestination),
            MemoryType.Readback);
        BufferRange range = new(0, checked((ulong)source.Length));
        using (MappedBuffer mapping = graphics.Map(upload, MapType.Write, range))
        {
            source.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        using CommandContext context = graphics.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        graphics.Begin(context);
        graphics.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, range.Size));
        using RecordedCommands recorded = graphics.End(context);
        RecordedCommands[] commands = [recorded];
        QueueSubmitDesc submit = new([], [], commands, [], []);
        QueueCompletion completion = graphics.Submit(graphics.GetQueue(device, QueueType.Copy), submit);
        Assert.Equal(WaitStatus.Completed, graphics.WaitCpu(completion, TimeSpan.FromSeconds(10)));

        byte[] result = new byte[source.Length];
        using MappedBuffer read = graphics.Map(readback, MapType.Read, range);
        read.Invalidate(range);
        read.Bytes.CopyTo(result);
        graphics.CollectCompleted(device);
        return result;
    }

    private static byte[] ExecuteInterface(IGraphicsBackend backend, ReadOnlySpan<byte> source) =>
        D3D12TestSupport.ExecuteCopyChain(backend, source);

    private static TypeLayoutReflection GetDataLayout(VariableLayoutReflection layout)
    {
        TypeLayoutReflection result = layout.TypeLayout.UnwrapArray();
        if (result.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
        {
            TypeLayoutReflection element = result.ElementTypeLayout.UnwrapArray();
            if (element != TypeLayoutReflection.Null)
                result = element;
        }
        return result;
    }

    private static byte[] CreatePattern(int length)
    {
        var result = new byte[length];
        for (int index = 0; index < result.Length; index++)
            result[index] = unchecked((byte)(17 + index * 37));
        return result;
    }
}
