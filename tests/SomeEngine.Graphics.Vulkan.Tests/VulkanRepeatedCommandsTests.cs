using System.Numerics;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanRepeatedCommandsTests(VulkanTestCompiler compiler)
{
    [Fact]
    public void RepeatedCopiesReadUpdatedBufferContentsThroughNewSubmissionTickets()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        using Buffer source = backend.CreateBuffer(device, new(64, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(device, new(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        Assert.False(backend.TryRepeatCommands(context, out _));
        ulong previousCompletion = 0;
        for (int frame = 0; frame < 8; frame++)
        {
            byte value = checked((byte)(frame + 1));
            using (MappedBuffer mapping = backend.Map(source, MapType.Write, BufferRange.Whole))
            {
                mapping.Bytes.Fill(value);
                mapping.Flush(mapping.Range);
            }
            RecordedCommands commands;
            if (frame == 0)
            {
                backend.Begin(context);
                backend.CopyBuffer(context, new(source, 0, destination, 0, 64));
                commands = backend.End(context);
            }
            else
                Assert.True(backend.TryRepeatCommands(context, out commands));
            using (commands)
            {
                QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
                Assert.True(completion.Value > previousCompletion);
                previousCompletion = completion.Value;
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                Assert.Throws<InvalidOperationException>(() => backend.Submit(queue, new([], [], [commands], [], [])));
            }
            using MappedBuffer result = backend.Map(destination, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.All(result.Bytes.ToArray(), item => Assert.Equal(value, item));
        }
        backend.Begin(context);
        backend.CopyBuffer(context, new(source, 0, destination, 0, 32));
        backend.Discard(context);
        Assert.False(backend.TryRepeatCommands(context, out _));
        backend.Begin(context);
        using RecordedCommands empty = backend.End(context);
        Assert.Equal(RecordedCommandsStatus.Executable, empty.Status);
    }

    [Fact]
    public void ReleasedCopyResourcesAllowANewRecording()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        using Buffer destination = backend.CreateBuffer(device, new(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using (Buffer source = backend.CreateBuffer(device, new(64, BufferUsages.CopySource), MemoryType.Upload))
        {
            backend.Begin(context);
            backend.CopyBuffer(context, new(source, 0, destination, 0, 64));
            using RecordedCommands commands = backend.End(context);
            SubmitAndWait(backend, device, commands);
        }
        Assert.False(backend.TryRepeatCommands(context, out _));
        backend.Begin(context);
        using RecordedCommands empty = backend.End(context);
        Assert.Equal(RecordedCommandsStatus.Executable, empty.Status);
    }

    [Fact]
    public void RepeatedSubmissionRetainsResourcesAfterCallerDisposal()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        Buffer source = backend.CreateBuffer(device, new(4, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using (MappedBuffer mapping = backend.Map(source, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapping.Bytes, 73u);
            mapping.Flush(mapping.Range);
        }
        backend.Begin(context);
        backend.CopyBuffer(context, new(source, 0, destination, 0, 4));
        using (RecordedCommands commands = backend.End(context))
            SubmitAndWait(backend, device, commands);
        Assert.True(backend.TryRepeatCommands(context, out RecordedCommands repeated));
        using (repeated)
        {
            source.Dispose();
            context.Dispose();
            SubmitAndWait(backend, device, repeated);
        }
        Assert.Equal(73u, ReadUInt(backend, destination));
    }

    [Fact]
    public void RepeatedTransientBindingsPreserveEachDispatchData()
    {
        const string source = """
            StructuredBuffer<uint> Input;
            RWStructuredBuffer<uint> Output;
            uint Bias;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { Output[0] = Input[0] + Bias; }
            """;
        VulkanTestShader shader = compiler.Compile(source, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer input = backend.CreateBuffer(device, new(4, BufferUsages.ShaderRead), MemoryType.Upload);
        using BufferSrv inputView = backend.CreateBufferSrv(device, new(input, BufferRange.Whole, StructureStride: 4));
        using Buffer first = backend.CreateBuffer(device, new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using Buffer second = backend.CreateBuffer(device, new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav firstView = backend.CreateBufferUav(device, new(first, BufferRange.Whole, StructureStride: 4));
        using BufferUav secondView = backend.CreateBufferUav(device, new(second, BufferRange.Whole, StructureStride: 4));
        using Buffer readback = backend.CreateBuffer(device, new(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        byte[] firstOrdinary = new byte[16], secondOrdinary = new byte[16];
        BitConverter.TryWriteBytes(firstOrdinary, 17u);
        BitConverter.TryWriteBytes(secondOrdinary, 29u);
        for (uint frame = 0; frame < 4; frame++)
        {
            using (MappedBuffer mapping = backend.Map(input, MapType.Write, BufferRange.Whole))
            {
                BitConverter.TryWriteBytes(mapping.Bytes, frame + 5);
                mapping.Flush(mapping.Range);
            }
            RecordedCommands commands;
            if (frame == 0)
            {
                backend.Begin(context);
                backend.SetPipeline(context, pipeline);
                backend.Barrier(context, new BufferBarrier(input, input.InitialSync, PipelineSync.ComputeShading,
                    input.InitialAccess, ResourceAccess.ShaderResource));
                foreach (Buffer output in new[] { first, second })
                    backend.Barrier(context, new BufferBarrier(output, PipelineSync.Copy, PipelineSync.ComputeShading,
                        ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
                backend.SetTransientParameterBindings(context,
                    new(globals, [ResourceBinding.ReadOnlyBuffer(inputView), ResourceBinding.WritableBuffer(firstView)], firstOrdinary));
                backend.Dispatch(context, new(1, 1, 1));
                backend.SetTransientParameterBindings(context,
                    new(globals, [ResourceBinding.ReadOnlyBuffer(inputView), ResourceBinding.WritableBuffer(secondView)], secondOrdinary));
                backend.Dispatch(context, new(1, 1, 1));
                foreach (Buffer output in new[] { first, second })
                    backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                        ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new(first, 0, readback, 0, 4));
                backend.CopyBuffer(context, new(second, 0, readback, 4, 4));
                commands = backend.End(context);
            }
            else
                Assert.True(backend.TryRepeatCommands(context, out commands));
            using (commands)
                SubmitAndWait(backend, device, commands);
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.Equal(frame + 22, BitConverter.ToUInt32(result.Bytes));
            Assert.Equal(frame + 34, BitConverter.ToUInt32(result.Bytes[4..]));
        }
    }

    [Fact]
    public void UpdatingPersistentBindingsStartsANewComputeRecording()
    {
        const string source = """
            RWStructuredBuffer<uint> Output;
            uint Bias;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { Output[0] = Bias; }
            """;
        VulkanTestShader shader = compiler.Compile(source, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device, new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device, new(output, BufferRange.Whole, StructureStride: 4));
        using Buffer readback = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        byte[] ordinary = new byte[16];
        BitConverter.TryWriteBytes(ordinary, 17u);
        using PersistentParameterBindings parameters = backend.CreatePersistentParameterBindings(device, pipeline,
            new(globals, [ResourceBinding.WritableBuffer(outputView)], ordinary));
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using (RecordedCommands commands = Record())
            SubmitAndWait(backend, device, commands);
        Assert.True(backend.TryRepeatCommands(context, out RecordedCommands repeated));
        using (repeated)
            SubmitAndWait(backend, device, repeated);
        Assert.Equal(17u, ReadUInt(backend, readback));
        BitConverter.TryWriteBytes(ordinary, 29u);
        backend.UpdatePersistentParameterBindings(parameters,
            new(globals, [ResourceBinding.WritableBuffer(outputView)], ordinary));
        Assert.False(backend.TryRepeatCommands(context, out _));
        using (RecordedCommands commands = Record())
            SubmitAndWait(backend, device, commands);
        Assert.True(backend.TryRepeatCommands(context, out RecordedCommands updated));
        using (updated)
            SubmitAndWait(backend, device, updated);
        Assert.Equal(29u, ReadUInt(backend, readback));

        RecordedCommands Record()
        {
            backend.Begin(context);
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetPersistentParameterBindings(context, parameters);
            backend.Dispatch(context, new(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, 4));
            return backend.End(context);
        }
    }

    [Fact]
    public void PublishingBindlessBindingsStartsANewComputeRecording()
    {
        const string source = """
            StructuredBuffer<uint> Input[];
            RWStructuredBuffer<uint> Output;
            uint InputIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { Output[0] = Input[InputIndex][0]; }
            """;
        VulkanTestShader shader = compiler.Compile(source, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        using Buffer first = backend.CreateBuffer(device, new(4, BufferUsages.ShaderRead), MemoryType.Upload);
        using Buffer second = backend.CreateBuffer(device, new(4, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(first, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapping.Bytes, 7u);
            mapping.Flush(mapping.Range);
        }
        using (MappedBuffer mapping = backend.Map(second, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapping.Bytes, 19u);
            mapping.Flush(mapping.Range);
        }
        using BufferSrv firstView = backend.CreateBufferSrv(device, new(first, BufferRange.Whole, StructureStride: 4));
        using BufferSrv secondView = backend.CreateBufferSrv(device, new(second, BufferRange.Whole, StructureStride: 4));
        using DescriptorTable table = backend.CreateDescriptorTable(device, [new(ResourceBindingType.BufferSrv, StructureStride: 4)]);
        backend.WriteDescriptor(table, 0, ResourceBinding.ReadOnlyBuffer(firstView));
        backend.PublishDescriptors(device);
        byte[] ordinary = new byte[16];
        BitConverter.TryWriteBytes(ordinary, backend.GetDescriptorIndex(table, 0).Value);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device, new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device, new(output, BufferRange.Whole, StructureStride: 4));
        using Buffer readback = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using (RecordedCommands commands = Record())
            SubmitAndWait(backend, device, commands);
        Assert.True(backend.TryRepeatCommands(context, out RecordedCommands repeated));
        using (repeated)
            SubmitAndWait(backend, device, repeated);
        Assert.Equal(7u, ReadUInt(backend, readback));
        backend.WriteDescriptor(table, 0, ResourceBinding.ReadOnlyBuffer(secondView));
        backend.PublishDescriptors(device);
        Assert.False(backend.TryRepeatCommands(context, out _));
        using (RecordedCommands commands = Record())
            SubmitAndWait(backend, device, commands);
        Assert.True(backend.TryRepeatCommands(context, out RecordedCommands updated));
        using (updated)
            SubmitAndWait(backend, device, updated);
        Assert.Equal(19u, ReadUInt(backend, readback));

        RecordedCommands Record()
        {
            backend.Begin(context);
            foreach (Buffer input in new[] { first, second })
                backend.Barrier(context, new BufferBarrier(input, input.InitialSync, PipelineSync.ComputeShading,
                    input.InitialAccess, ResourceAccess.ShaderResource));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(context, new(globals, [ResourceBinding.WritableBuffer(outputView)], ordinary));
            backend.Dispatch(context, new(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, 4));
            return backend.End(context);
        }
    }

    [Fact]
    public void RepeatedColorClearsUseTheirRecordedAttachment()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        using Texture texture = backend.CreateTexture(device, new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32UInt, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using Buffer readback = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.ClearTexture(context, texture, range, new Vector4(7, 0, 0, 0));
        backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new(readback, 0, 4, 1, texture, 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1));
        using (RecordedCommands commands = backend.End(context))
            SubmitAndWait(backend, device, commands);
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.True(backend.TryRepeatCommands(context, out RecordedCommands repeated));
            using (repeated)
                SubmitAndWait(backend, device, repeated);
            Assert.Equal(7u, ReadUInt(backend, readback));
        }
    }

    [Fact]
    public void RepeatedDepthClearsPreserveTheAttachment()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        using Texture texture = backend.CreateTexture(device, new(TextureDimension.Texture2D, 4, 4, 1, 1, 1, 1,
            Format.D32Float, TextureUsages.DepthStencilAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Depth);
        using Buffer readback = backend.CreateBuffer(device, new(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        for (int frame = 0; frame < 4; frame++)
        {
            RecordedCommands commands;
            if (frame == 0)
            {
                backend.Begin(context);
                backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.None, PipelineSync.DepthStencil,
                    ResourceAccess.NoAccess, ResourceAccess.DepthStencilWrite, TextureLayout.Undefined, TextureLayout.DepthStencilWrite));
                backend.ClearDepthStencil(context, texture, range, .25f);
                backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.DepthStencil, PipelineSync.Copy,
                    ResourceAccess.DepthStencilWrite, ResourceAccess.CopySource, TextureLayout.DepthStencilWrite, TextureLayout.CopySource));
                backend.CopyTextureToBuffer(context, new(readback, 0, 16, 4, texture, 0, 0, TextureAspects.Depth, 0, 0, 0, 4, 4, 1));
                commands = backend.End(context);
            }
            else
                Assert.True(backend.TryRepeatCommands(context, out commands));
            using (commands)
                SubmitAndWait(backend, device, commands);
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.Equal(.25f, BitConverter.ToSingle(result.Bytes));
        }
    }

    [Fact]
    public void RepeatedCopiesCaptureTheCurrentSparseBacking()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)], requiredFeatures: DeviceFeatures.SparseResources));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Buffer sparse = backend.CreateReservedBuffer(device, new(128 * 1024, BufferUsages.CopySource | BufferUsages.CopyDestination));
        SparseResourceInfo info = backend.GetSparseResourceInfo(sparse);
        using Heap firstHeap = backend.CreateHeap(device, new(info.Alignment, info.Alignment, MemoryType.DeviceLocal, HeapFlags.Buffers));
        using Heap secondHeap = backend.CreateHeap(device, new(info.Alignment, info.Alignment, MemoryType.DeviceLocal, HeapFlags.Buffers));
        using Buffer upload = backend.CreateBuffer(device, new(4, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext writes = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using CommandContext copies = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        SparseTileRegion tile = new(new(0, 0, 0, 0), 0, 0, 0, 1, Boxed: false);
        QueueCompletion firstMapping = backend.UpdateSparseMappings(queue, [new(sparse, tile, SparseMappingType.Mapped, firstHeap, 0)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(firstMapping, TimeSpan.FromSeconds(10)));
        Write(7);
        backend.Begin(copies);
        backend.CopyBuffer(copies, new(sparse, 0, readback, 0, 4));
        using (RecordedCommands commands = backend.End(copies))
            SubmitAndWait(backend, device, commands);
        Assert.Equal(7u, ReadUInt(backend, readback));
        QueueCompletion secondMapping = backend.UpdateSparseMappings(queue, [new(sparse, tile, SparseMappingType.Mapped, secondHeap, 0)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(secondMapping, TimeSpan.FromSeconds(10)));
        firstHeap.Dispose();
        Write(19);
        Assert.True(backend.TryRepeatCommands(copies, out RecordedCommands repeated));
        using (repeated)
            SubmitAndWait(backend, device, repeated);
        Assert.Equal(19u, ReadUInt(backend, readback));

        void Write(uint value)
        {
            using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
            {
                BitConverter.TryWriteBytes(mapping.Bytes, value);
                mapping.Flush(mapping.Range);
            }
            backend.Begin(writes);
            backend.CopyBuffer(writes, new(upload, 0, sparse, 0, 4));
            backend.Barrier(writes, new BufferBarrier(sparse, PipelineSync.Copy, PipelineSync.Copy,
                ResourceAccess.CopyDestination, ResourceAccess.CopySource));
            using RecordedCommands commands = backend.End(writes);
            SubmitAndWait(backend, device, commands);
        }
    }

    [Fact]
    public void RepeatedDrawsPreserveMaterialParameters()
    {
        const string source = """
            float4 Tint;
            [shader("vertex")]
            float4 vertexMain(uint id : SV_VertexID) : SV_Position
            {
                const float2 positions[3] = { float2(-1, -1), float2(3, -1), float2(-1, 3) };
                return float4(positions[id], 0, 1);
            }
            [shader("fragment")]
            float4 pixelMain() : SV_Target0 { return Tint; }
            """;
        VulkanTestShader shader = compiler.Compile(source, ("vertexMain", SlangStage.Vertex), ("pixelMain", SlangStage.Fragment));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new(default, [new(QueueType.Graphics)]));
        using Pipeline pipeline = backend.CreateGraphicsPipeline(device, new(
            shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0), shader.Entries[1], shader.GetEntryCode(1),
            [], [], PrimitiveTopology.TriangleList, StripCut.Disabled, new(Cull: CullType.None), new(), new(),
            new([new BlendAttachmentState()]), new([Format.R8G8B8A8UNorm], null), DynamicStates.Viewport | DynamicStates.Scissor));
        byte[] tint = new byte[16];
        BitConverter.TryWriteBytes(tint, 1f);
        BitConverter.TryWriteBytes(tint.AsSpan(12), 1f);
        using PersistentParameterBindings parameters = backend.CreatePersistentParameterBindings(device, pipeline, new(globals, [], tint));
        using Texture texture = backend.CreateTexture(device, new(TextureDimension.Texture2D, 4, 4, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new(texture, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(device, new(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        for (int frame = 0; frame < 4; frame++)
        {
            RecordedCommands commands;
            if (frame == 0)
            {
                backend.Begin(context);
                backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.None, PipelineSync.RenderTarget,
                    ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
                backend.BeginRendering(context, new([new ColorAttachmentDesc(attachment, LoadType.Clear, StoreType.Store, Vector4.Zero)], null, 4, 4));
                backend.SetPipeline(context, pipeline);
                backend.SetPersistentParameterBindings(context, parameters);
                backend.SetViewports(context, [new Viewport(0, 0, 4, 4)]);
                backend.SetScissors(context, [new ScissorRect(0, 0, 4, 4)]);
                backend.Draw(context, new(3, 1, 0, 0));
                backend.EndRendering(context);
                backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.RenderTarget, PipelineSync.Copy,
                    ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
                backend.CopyTextureToBuffer(context, new(readback, 0, 16, 4, texture, 0, 0, TextureAspects.Color, 0, 0, 0, 4, 4, 1));
                commands = backend.End(context);
            }
            else
                Assert.True(backend.TryRepeatCommands(context, out commands));
            using (commands)
                SubmitAndWait(backend, device, commands);
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, result.Bytes.Slice(40, 4).ToArray());
        }
    }

    private static void SubmitAndWait(IGraphicsBackend backend, Device device, RecordedCommands commands)
    {
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    private static uint ReadUInt(IGraphicsBackend backend, Buffer buffer)
    {
        using MappedBuffer result = backend.Map(buffer, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        return BitConverter.ToUInt32(result.Bytes);
    }
}
