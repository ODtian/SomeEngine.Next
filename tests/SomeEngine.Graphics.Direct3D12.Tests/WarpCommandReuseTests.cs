using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpCommandReuseTests
{
    [Fact]
    public void Descriptor_storage_tracks_resource_changes_and_heap_replacement_while_constants_change()
    {
        const string source = """
            RWStructuredBuffer<uint> output;
            struct Bias { uint value; };
            ConstantBuffer<Bias> bias;
            uint value;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID) { output[id.x] = value + bias.value; }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "descriptor_content_reuse", source, [new("computeMain", SlangStage.Compute)], "sm_6_2");
        ShaderVariableLayout layout = shader.Reflection.GetGlobalParamsVarLayout() ?? ShaderVariableLayout.Null;
        ShaderTypeLayout dataLayout = layout.TypeLayout.UnwrapArray();
        if (dataLayout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            dataLayout = dataLayout.ElementTypeLayout.UnwrapArray();
        int ordinarySize = checked((int)dataLayout.GetSize(SlangParameterCategory.Uniform));
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Buffer constants = backend.CreateBuffer(device, new BufferDesc(512, BufferUsages.Constant), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(constants, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.Write(mapping.Bytes, 17u);
            MemoryMarshal.Write(mapping.Bytes[256..], 29u);
        }
        using Buffer first = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using Buffer second = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav firstView = backend.CreateBufferUav(device, new(first, BufferRange.Whole, Format.R32UInt));
        BufferUav secondView = backend.CreateBufferUav(device, new(second, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Compute);

        try
        {
            for (int frame = 1; frame <= 6; frame++)
            {
                if (frame == 6)
                {
                    secondView.Dispose();
                    secondView = backend.CreateBufferUav(device, new(second, BufferRange.Whole, Format.R32UInt));
                }
                Buffer output = frame < 3 ? first : second;
                ResourceBinding[] resources = [ResourceBinding.WritableBuffer(frame < 3 ? firstView : secondView),
                    ResourceBinding.ConstantBuffer(constants, new(frame < 3 ? 0ul : 256ul, 256))];
                backend.Begin(context, new CommandRecordingDesc(frame < 4 ? 8u : 64u, 0, 8));
                backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
                    ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
                backend.SetPipeline(context, pipeline);
                byte[] ordinary = new byte[ordinarySize];
                MemoryMarshal.Write(ordinary, (uint)(frame * 10 + 1));
                backend.SetTransientParameterBindings(context, new(layout, resources, ordinary));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                    ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
                MemoryMarshal.Write(ordinary, (uint)(frame * 10 + 2));
                backend.SetTransientParameterBindings(context, new(layout, resources, ordinary));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new(output, 0, readback, 0, 4));
                using RecordedCommands commands = backend.End(context);
                if (frame == 6) constants.Dispose();
                QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                using MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, 4));
                mapped.Invalidate(new(0, 4));
                Assert.Equal((uint)(frame * 10 + 2) + (frame < 3 ? 17u : 29u), MemoryMarshal.Read<uint>(mapped.Bytes));
                backend.CollectCompleted(device);
            }
        }
        finally
        {
            secondView.Dispose();
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(129)]
    public void Descriptor_array_updates_survive_growth_during_recording(int count)
    {
        string source = $$"""
            RWStructuredBuffer<uint> outputs[{{count}}];
            uint value;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                for (uint i = 0; i < {{count}}; i++) outputs[i][0] = value;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            $"descriptor_array_{count}", source, [new("computeMain", SlangStage.Compute)], "sm_6_6");
        ShaderVariableLayout layout = shader.Reflection.GetGlobalParamsVarLayout() ?? ShaderVariableLayout.Null;
        ShaderTypeLayout dataLayout = layout.TypeLayout.UnwrapArray();
        if (dataLayout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            dataLayout = dataLayout.ElementTypeLayout.UnwrapArray();
        byte[] ordinary = new byte[checked((int)dataLayout.GetSize(SlangParameterCategory.Uniform))];
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device, new(shader.Program, shader.GetEntryPoint(0)));
        using Buffer first = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using Buffer second = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav firstView = backend.CreateBufferUav(device, new(first, BufferRange.Whole, StructureStride: 4));
        using BufferUav secondView = backend.CreateBufferUav(device, new(second, BufferRange.Whole, StructureStride: 4));
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Compute);

        for (int frame = 1; frame <= 2; frame++)
        {
            ResourceBinding[] resources = Enumerable.Repeat(ResourceBinding.WritableBuffer(firstView), count).ToArray();
            backend.Begin(context, new CommandRecordingDesc(2, 0, (uint)(count + 4)));
            backend.Barrier(context, new BufferBarrier(first, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            backend.Barrier(context, new BufferBarrier(second, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            MemoryMarshal.Write(ordinary, (uint)(frame * 10 + 1));
            backend.SetTransientParameterBindings(context, new(layout, resources, ordinary));
            backend.Dispatch(context, new(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(first, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
            resources[0] = ResourceBinding.WritableBuffer(secondView);
            MemoryMarshal.Write(ordinary, (uint)(frame * 10 + 2));
            backend.SetTransientParameterBindings(context, new(layout, resources, ordinary));
            backend.Dispatch(context, new(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(first, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.Barrier(context, new BufferBarrier(second, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(first, 0, readback, 0, 4));
            backend.CopyBuffer(context, new(second, 0, readback, 4, 4));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, 8));
            mapped.Invalidate(new(0, 8));
            Assert.All(MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray(),
                value => Assert.Equal((uint)(frame * 10 + 2), value));
            backend.CollectCompleted(device);
        }
    }

    [Theory]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(600)]
    public void Large_barrier_batches_preserve_buffer_contents(int count)
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using Buffer upload = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc((ulong)count * 4, BufferUsages.CopyDestination), MemoryType.Readback);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
            MemoryMarshal.Write(mapping.Bytes, 42u);
        var buffers = new List<Buffer>();

        try
        {
            var barriers = new BufferBarrier[count];
            for (int index = 0; index < count; index++)
            {
                Buffer buffer = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.CopyDestination));
                buffers.Add(buffer);
                barriers[index] = new(buffer, PipelineSync.Copy, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, ResourceAccess.CopyDestination);
            }
            backend.Begin(context);
            foreach (Buffer buffer in buffers)
                backend.CopyBuffer(context, new(upload, 0, buffer, 0, 4));
            backend.Barrier(context, new BarrierBatch([], [], barriers, [], []));
            for (int index = 0; index < buffers.Count; index++)
            {
                backend.Barrier(context, new BufferBarrier(buffers[index], PipelineSync.Copy, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new(buffers[index], 0, readback, (ulong)index * 4, 4));
            }
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics),
                new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            Assert.All(MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray(), value => Assert.Equal(42u, value));
        }
        finally
        {
            foreach (Buffer buffer in buffers) buffer.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Buffer_queue_transfers_preserve_contents(bool batched)
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer buffer = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext source = backend.CreateCommandContext(device, new(QueueType.Copy, 0, 1));
        using CommandContext target = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));

        for (uint value = 1; value <= 3; value++)
        {
            using (MappedBuffer mapping = backend.Map(upload, MapType.Write, new(0, 4)))
                MemoryMarshal.Write(mapping.Bytes, in value);
            backend.Begin(source);
            backend.CopyBuffer(source, new(upload, 0, buffer, 0, 4));
            QueueRelease release = new(buffer, null, PipelineSync.Copy, ResourceAccess.CopyDestination, null, QueueType.Compute);
            if (batched) backend.Barrier(source, new BarrierBatch([], [], [], [], [release]));
            else backend.Barrier(source, release);
            using RecordedCommands written = backend.End(source);
            QueueCompletion ready = backend.Submit(backend.GetQueue(device, QueueType.Copy), new([], [], [written], [], []));
            backend.Begin(target);
            QueueAcquire acquire = new(buffer, null, QueueType.Copy, PipelineSync.Copy, ResourceAccess.CopySource, null);
            if (batched) backend.Barrier(target, new BarrierBatch([], [acquire], [], [], []));
            else backend.Barrier(target, acquire);
            backend.Barrier(target, new BufferBarrier(readback, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
            backend.CopyBuffer(target, new(buffer, 0, readback, 0, 4));
            using RecordedCommands copied = backend.End(target);
            QueueCompletion done = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([ready], [], [copied], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(done, TimeSpan.FromSeconds(10)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, new(0, 4));
            Assert.Equal(value, MemoryMarshal.Read<uint>(result.Bytes));
        }
    }

    [Fact]
    public unsafe void Completion_waits_are_covered_per_target_queue_and_external_waits_survive_rewinds()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using ExternalTimeline gate = backend.CreateExternalTimeline(device, 0);
        Queue source = backend.GetQueue(device, QueueType.Compute);
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        Queue copy = backend.GetQueue(device, QueueType.Copy);
        QueueCompletion first = backend.Submit(source, new([], [new(gate, 1)], [], [], []));
        QueueCompletion second = backend.Submit(source, new([], [], [], [], []));

        try
        {
            QueueCompletion graphicsDone;
            QueueCompletion copyDone;
            try
            {
                _ = backend.Submit(graphics, new([second], [], [], [], []));
                _ = backend.Submit(graphics, new([first], [], [], [], []));
                graphicsDone = backend.Submit(graphics, new([second], [], [], [], []));
                copyDone = backend.Submit(copy, new([first], [], [], [], []));
                Assert.Equal(WaitStatus.Timeout, backend.WaitCpu(graphicsDone, TimeSpan.FromMilliseconds(10)));
                Assert.Equal(WaitStatus.Timeout, backend.WaitCpu(copyDone, TimeSpan.FromMilliseconds(10)));
            }
            finally
            {
                Assert.True(backend.GetNativeTimeline(gate)->Signal(1) >= 0);
            }
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(graphicsDone, TimeSpan.FromSeconds(10)));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(copyDone, TimeSpan.FromSeconds(10)));
            _ = backend.Submit(graphics, new([second], [], [], [], []));
            _ = backend.Submit(copy, new([second], [], [], [], []));

            for (int iteration = 0; iteration < 2; iteration++)
            {
                Assert.True(backend.GetNativeTimeline(gate)->Signal(0) >= 0);
                QueueCompletion completion;
                try
                {
                    completion = backend.Submit(graphics, new([], [new(gate, 1)], [], [], []));
                    Assert.Equal(WaitStatus.Timeout, backend.WaitCpu(completion, TimeSpan.FromMilliseconds(10)));
                }
                finally
                {
                    Assert.True(backend.GetNativeTimeline(gate)->Signal(1) >= 0);
                }
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            }
        }
        finally
        {
            _ = backend.GetNativeTimeline(gate)->Signal(1);
        }
    }

}
