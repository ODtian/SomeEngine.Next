using System.Buffers.Binary;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class IndirectCommandTests
{
    [Fact]
    public void Draw_indirect_validates_count_stride_offsets_ranges_states_and_scope()
    {
        using var device = new Device();
        (PipelineHandle pipeline, _, _, _) = PortableRhiTestSupport.CreateRasterPipeline(device);
        (TextureHandle target, TextureViewHandle view) = PortableRhiTestSupport.CreateRenderTarget(device);

        byte[] cpuArguments = DrawArguments(prefix: 8, stride: 20, (3, 1), (6, 2));
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            (_, BufferHandle arguments) = PortableRhiTestSupport.StageBuffer(
                device, context, cpuArguments, BufferUsage.Indirect, ResourceState.IndirectArgument);
            context.Barriers([ResourceBarrier.Transition(target.Resource, ResourceState.Common, ResourceState.RenderTarget)]);
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            context.DrawIndirect(arguments, 8, 2, 20);
            context.EndRendering();
            Submit(device, context, QueueType.Graphics);
        }
        Assert.Equal(2, device.Statistics.Draws);

        byte[] gpuArguments = DrawArguments(prefix: 0, stride: 16, (3, 1), (3, 1), (3, 1));
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            (_, BufferHandle arguments) = PortableRhiTestSupport.StageBuffer(
                device, context, gpuArguments, BufferUsage.Indirect, ResourceState.IndirectArgument);
            (_, BufferHandle count) = PortableRhiTestSupport.StageBuffer(
                device, context, PortableRhiTestSupport.UInt32Words(99, 2), BufferUsage.Indirect, ResourceState.IndirectArgument);
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            context.DrawIndirect(arguments, 0, 3, 16, count, 4);
            context.EndRendering();
            Submit(device, context, QueueType.Graphics);
        }
        Assert.Equal(4, device.Statistics.Draws);

        byte[] shared = DrawArguments(prefix: 8, stride: 16, (1, 1), (1, 1), (1, 1));
        BinaryPrimitives.WriteUInt32LittleEndian(shared, 2);
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            (_, BufferHandle argumentsAndCount) = PortableRhiTestSupport.StageBuffer(
                device, context, shared, BufferUsage.Indirect, ResourceState.IndirectArgument);
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            context.DrawIndirect(argumentsAndCount, 8, 3, 16, argumentsAndCount, 0);
            context.EndRendering();
            Submit(device, context, QueueType.Graphics);
        }
        Assert.Equal(6, device.Statistics.Draws);

        BufferHandle wrongUsage = device.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
        BufferHandle wrongState = device.CreateBuffer(new BufferDesc(64, BufferUsage.Indirect));
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            Assert.Throws<InvalidOperationException>(() => context.DrawIndirect(wrongUsage, 0, 1, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => context.DrawIndirect(wrongState, 0, 1, 12));
            Assert.Throws<ArgumentOutOfRangeException>(() => context.DrawIndirect(wrongState, 2, 1, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => context.DrawIndirect(wrongState, 0, 0, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => context.DrawIndirect(wrongState, 48, 2, 16));
            Assert.Throws<ArgumentException>(() => context.DrawIndirect(wrongState, 0, 1, 16, default, 4));
            Assert.Throws<ArgumentException>(() => context.DrawIndirect(wrongState, 0, 1, 16, wrongState, 0));
            context.DrawIndirect(wrongState, 0, 1, 16);
            context.EndRendering();
            CommandListHandle list = context.Finish();
            Assert.Throws<InvalidOperationException>(() => device.Submit(QueueType.Graphics, [list]));
            device.DiscardCommandList(list);
        }

        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            Assert.Throws<InvalidOperationException>(() => context.DrawIndirect(wrongState, 0, 1, 16));
        }
    }

    [Fact]
    public void Draw_indexed_indirect_validates_index_state_count_stride_and_ranges()
    {
        using var device = new Device();
        (PipelineHandle pipeline, _, _, _) = PortableRhiTestSupport.CreateRasterPipeline(device);
        (TextureHandle target, TextureViewHandle view) = PortableRhiTestSupport.CreateRenderTarget(device);
        BufferHandle indexBuffer = device.CreateBuffer(new BufferDesc(64, BufferUsage.Index));

        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            (_, BufferHandle arguments) = PortableRhiTestSupport.StageBuffer(
                device, context, IndexedArguments(4, 20, (3, 1), (6, 1)), BufferUsage.Indirect, ResourceState.IndirectArgument);
            context.Barriers([
                ResourceBarrier.Transition(target.Resource, ResourceState.Common, ResourceState.RenderTarget),
                ResourceBarrier.Transition(indexBuffer.Resource, ResourceState.Common, ResourceState.IndexBuffer),
            ]);
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            context.SetIndexBuffer(indexBuffer, 0, IndexFormat.UInt16);
            context.DrawIndexedIndirect(arguments, 4, 2, 20);
            context.EndRendering();
            Submit(device, context, QueueType.Graphics);
        }
        Assert.Equal(2, device.Statistics.Draws);

        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            (_, BufferHandle arguments) = PortableRhiTestSupport.StageBuffer(
                device, context, IndexedArguments(0, 20, (3, 1), (3, 1)), BufferUsage.Indirect, ResourceState.IndirectArgument);
            (_, BufferHandle count) = PortableRhiTestSupport.StageBuffer(
                device, context, PortableRhiTestSupport.UInt32Words(1), BufferUsage.Indirect, ResourceState.IndirectArgument);
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            context.SetIndexBuffer(indexBuffer, 0, IndexFormat.UInt16);
            context.DrawIndexedIndirect(arguments, 0, 2, 20, count, 0);
            context.EndRendering();
            Submit(device, context, QueueType.Graphics);
        }
        Assert.Equal(3, device.Statistics.Draws);

        BufferHandle argumentsForValidation = device.CreateBuffer(new BufferDesc(20, BufferUsage.Indirect));
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            Assert.Throws<InvalidOperationException>(() =>
                context.DrawIndexedIndirect(argumentsForValidation, 0, 1, 20));
            context.SetIndexBuffer(indexBuffer, 0, IndexFormat.UInt16);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                context.DrawIndexedIndirect(argumentsForValidation, 0, 1, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                context.DrawIndexedIndirect(argumentsForValidation, 0, 2, 20));
            context.EndRendering();
        }

        BufferHandle indexWrongState = device.CreateBuffer(new BufferDesc(64, BufferUsage.Index));
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            (_, BufferHandle arguments) = PortableRhiTestSupport.StageBuffer(
                device, context, IndexedArguments(0, 20, (3, 1)), BufferUsage.Indirect, ResourceState.IndirectArgument);
            context.BeginRendering(PortableRhiTestSupport.Rendering(view));
            context.SetPipeline(pipeline);
            context.SetIndexBuffer(indexWrongState, 0, IndexFormat.UInt16);
            context.DrawIndexedIndirect(arguments, 0, 1, 20);
            context.EndRendering();
            CommandListHandle list = context.Finish();
            Assert.Throws<InvalidOperationException>(() => device.Submit(QueueType.Graphics, [list]));
            device.DiscardCommandList(list);
        }
    }

    [Fact]
    public void Dispatch_indirect_validates_count_stride_offsets_ranges_states_queue_and_scope()
    {
        using var device = new Device();
        (PipelineHandle pipeline, _, _) = PortableRhiTestSupport.CreateComputePipeline(device);

        using (ICommandContext context = device.AcquireCommandContext(QueueType.Compute))
        {
            (_, BufferHandle arguments) = PortableRhiTestSupport.StageBuffer(
                device, context, DispatchArguments(8, 16, (2, 1, 1), (3, 1, 1)), BufferUsage.Indirect, ResourceState.IndirectArgument);
            context.SetPipeline(pipeline);
            context.DispatchIndirect(arguments, 8, 2, 16);
            Submit(device, context, QueueType.Compute);
        }
        Assert.Equal(2, device.Statistics.Dispatches);

        using (ICommandContext context = device.AcquireCommandContext(QueueType.Compute))
        {
            (_, BufferHandle arguments) = PortableRhiTestSupport.StageBuffer(
                device, context, DispatchArguments(0, 12, (1, 1, 1), (1, 1, 1), (1, 1, 1)), BufferUsage.Indirect, ResourceState.IndirectArgument);
            (_, BufferHandle count) = PortableRhiTestSupport.StageBuffer(
                device, context, PortableRhiTestSupport.UInt32Words(77, 2), BufferUsage.Indirect, ResourceState.IndirectArgument);
            context.SetPipeline(pipeline);
            context.DispatchIndirect(arguments, 0, 3, 12, count, 4);
            Submit(device, context, QueueType.Compute);
        }
        Assert.Equal(4, device.Statistics.Dispatches);

        BufferHandle validation = device.CreateBuffer(new BufferDesc(24, BufferUsage.Indirect));
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Compute))
        {
            context.SetPipeline(pipeline);
            Assert.Throws<ArgumentOutOfRangeException>(() => context.DispatchIndirect(validation, 0, 1, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => context.DispatchIndirect(validation, 2, 1, 12));
            Assert.Throws<ArgumentOutOfRangeException>(() => context.DispatchIndirect(validation, 0, 3, 12));
            context.DispatchIndirect(validation, 0, 1, 12);
            CommandListHandle list = context.Finish();
            Assert.Throws<InvalidOperationException>(() => device.Submit(QueueType.Compute, [list]));
            device.DiscardCommandList(list);
        }

        using (ICommandContext copy = device.AcquireCommandContext(QueueType.Copy))
        {
            Assert.Throws<InvalidOperationException>(() => copy.SetPipeline(pipeline));
            Assert.Throws<InvalidOperationException>(() => copy.DispatchIndirect(validation, 0, 1, 12));
        }

        (TextureHandle target, TextureViewHandle view) = PortableRhiTestSupport.CreateRenderTarget(device);
        using (ICommandContext graphics = device.AcquireCommandContext(QueueType.Graphics))
        {
            graphics.Barriers([ResourceBarrier.Transition(target.Resource, ResourceState.Common, ResourceState.RenderTarget)]);
            graphics.SetPipeline(pipeline);
            graphics.BeginRendering(PortableRhiTestSupport.Rendering(view));
            Assert.Throws<InvalidOperationException>(() => graphics.DispatchIndirect(validation, 0, 1, 12));
            graphics.EndRendering();
        }
    }

    private static void Submit(Device device, ICommandContext context, QueueType queue)
    {
        CommandListHandle list = context.Finish();
        GpuCompletion completion = device.Submit(queue, [list]);
        Assert.True(device.Wait(completion, TimeSpan.Zero));
    }

    private static byte[] DrawArguments(int prefix, int stride, params (uint VertexCount, uint InstanceCount)[] commands)
    {
        byte[] bytes = new byte[checked(prefix + stride * commands.Length)];
        for (int index = 0; index < commands.Length; index++)
        {
            Span<byte> record = bytes.AsSpan(prefix + index * stride, checked((int)DrawIndirectArguments.ByteSize));
            BinaryPrimitives.WriteUInt32LittleEndian(record, commands[index].VertexCount);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], commands[index].InstanceCount);
        }
        return bytes;
    }

    private static byte[] IndexedArguments(int prefix, int stride, params (uint IndexCount, uint InstanceCount)[] commands)
    {
        byte[] bytes = new byte[checked(prefix + stride * commands.Length)];
        for (int index = 0; index < commands.Length; index++)
        {
            Span<byte> record = bytes.AsSpan(prefix + index * stride, checked((int)DrawIndexedIndirectArguments.ByteSize));
            BinaryPrimitives.WriteUInt32LittleEndian(record, commands[index].IndexCount);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], commands[index].InstanceCount);
        }
        return bytes;
    }

    private static byte[] DispatchArguments(int prefix, int stride, params (uint X, uint Y, uint Z)[] commands)
    {
        byte[] bytes = new byte[checked(prefix + stride * commands.Length)];
        for (int index = 0; index < commands.Length; index++)
        {
            Span<byte> record = bytes.AsSpan(prefix + index * stride, checked((int)DispatchIndirectArguments.ByteSize));
            BinaryPrimitives.WriteUInt32LittleEndian(record, commands[index].X);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], commands[index].Y);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], commands[index].Z);
        }
        return bytes;
    }
}
