using System.Numerics;
using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class FrameResourceBindingTests
{
    [Fact]
    public void DeclaredExternalSlotsBindPerFrameResources()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphBufferId sourceSlot;
        GraphBufferId destinationSlot;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceSlot = edit.DeclareExternalBuffer(
                new BufferDesc(ByteCount, BufferUsages.CopySource),
                MemoryType.Upload);
            destinationSlot = edit.DeclareExternalBuffer(
                new BufferDesc(ByteCount, BufferUsages.CopyDestination),
                MemoryType.Readback);
            _ = edit.AddCopyPass<CopyState, byte>(
                "external slots",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyState(sourceSlot, destinationSlot, ByteCount),
                default,
                DeclareCopy,
                static (ref CopyPassCommandScope commands, in CopyState copy, in byte _) =>
                    RecordCopy(ref commands, copy));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.BindExternalBuffer(sourceSlot, source,
                [Endpoint(source, support.GraphicsQueue, ResourceContentState.Defined)]);
            frame.BindExternalBuffer(destinationSlot, destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);
    }

    [Fact]
    public void DynamicBufferAccessIdsDriveTheRecordedRange()
    {
        const ulong offset = 16;
        const ulong size = 32;
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphPassId pass;
        GraphBufferAccessId sourceAccess = default;
        GraphBufferAccessId destinationAccess = default;
        CopyState copy;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId sourceId = edit.RegisterExternalBuffer(source,
                [Endpoint(source, support.GraphicsQueue, ResourceContentState.Defined)]);
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            copy = new CopyState(sourceId, destinationId, size);
            pass = edit.AddCopyPass<CopyState, DynamicCopyFrame>(
                "dynamic range copy",
                PassQueueSelection.Exact(support.GraphicsQueue),
                copy,
                default,
                (ref PassDefinition definition, ref CopyState state) =>
                {
                    sourceAccess = definition.Read(
                        state.Source,
                        new BufferRange(0, 1),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource,
                        dynamicRange: true);
                    destinationAccess = definition.Write(
                        state.Destination,
                        new BufferRange(0, 1),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete,
                        dynamicRange: true);
                },
                static (ref CopyPassCommandScope commands,
                    in CopyState definition,
                    in DynamicCopyFrame frame) =>
                {
                    commands.CopyBuffer(new BufferCopy(
                        commands.GetBuffer(definition.Source),
                        frame.Offset,
                        commands.GetBuffer(definition.Destination),
                        frame.Offset,
                        frame.Size));
                });
            edit.Commit();
        }

        byte[] expected = new byte[checked((int)ByteCount)];
        for (int index = 0; index < expected.Length; index++)
            expected[index] = unchecked((byte)(index * 17 + 3));
        using (MappedBuffer mapped = support.Backend.Map(
            source,
            MapType.Write,
            new BufferRange(0, ByteCount)))
        {
            expected.CopyTo(mapped.Bytes);
            mapped.Flush(new BufferRange(0, ByteCount));
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, new DynamicCopyFrame(offset, size));
            frame.SetBufferRange(sourceAccess, new BufferRange(offset, size));
            frame.SetBufferRange(destinationAccess, new BufferRange(offset, size));
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);

        byte[] actual = new byte[checked((int)size)];
        using (MappedBuffer mapped = support.Backend.Map(
            destination,
            MapType.Read,
            new BufferRange(offset, size)))
        {
            mapped.Invalidate(new BufferRange(offset, size));
            mapped.Bytes.CopyTo(actual);
        }
        Assert.Equal(
            expected.AsSpan(checked((int)offset), checked((int)size)).ToArray(),
            actual);
    }

    [Fact]
    public void DynamicTextureAccessIdsDriveTheRecordedSubresource()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        GraphTextureAccessId textureAccess = default;
        GraphPassId pass;
        TextureWriteState state;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D,
                width: 16,
                height: 16,
                depth: 1,
                mipLevelCount: 3,
                arrayLayerCount: 1,
                sampleCount: 1,
                format: Format.R8G8B8A8UNorm,
                usages: TextureUsages.ColorAttachment));
            state = new TextureWriteState(texture);
            pass = edit.AddCopyPass<TextureWriteState, DynamicTextureWriteFrame>(
                "dynamic texture range",
                PassQueueSelection.Exact(support.GraphicsQueue),
                state,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition definition, ref TextureWriteState definitionState) =>
                {
                    textureAccess = definition.Write(
                        definitionState.Texture,
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.RenderTarget,
                        ResourceAccess.RenderTarget,
                        TextureLayout.RenderTarget,
                        WriteCoverage.Complete,
                        dynamicRange: true);
                },
                static (ref CopyPassCommandScope commands,
                    in TextureWriteState definitionState,
                    in DynamicTextureWriteFrame frame) =>
                {
                    Texture texture = commands.GetTexture(definitionState.Texture);
                    Assert.True(
                        texture.Info.Usages.HasFlag(TextureUsages.ColorAttachment),
                        $"Expected ColorAttachment usage, actual: {texture.Info.Usages}.");
                    commands.ClearTexture(
                        texture,
                        frame.Range,
                        new Vector4(0.25f, 0.5f, 0.75f, 1.0f));
                });
            edit.Commit();
        }

        TextureSubresourceRange range = new(1, 1, 0, 1, TextureAspects.Color);
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, new DynamicTextureWriteFrame(range));
            frame.SetTextureRange(textureAccess, range);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);
    }
}
