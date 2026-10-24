using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class TextureBarrierCompactionTests
{
    [Fact]
    public void AdjacentRasterPassesKeepTheirOwnClearAndLoadOperations()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(256, BufferUsages.CopyDestination), MemoryType.Readback);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
            GraphColorAttachmentViewId view = edit.CreateColorAttachmentView(texture, range,
                Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D);
            GraphPassId previous = default;
            for (int index = 0; index < 3; index++)
            {
                int current = index;
                GraphPassId pass = edit.AddRasterPass<GraphColorAttachmentViewId, byte>(
                    "adjacent raster " + index, PassQueueSelection.Exact(support.GraphicsQueue), view,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    (ref PassDefinition access, ref GraphColorAttachmentViewId target) =>
                        access.ColorAttachment(0, target, current == 2 ? LoadType.Load : LoadType.Clear,
                            StoreType.Store, current == 2 ? WriteCoverage.Partial : WriteCoverage.Complete,
                            current == 0 ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1)),
                    static (ref RasterPassCommandScope commands, in GraphColorAttachmentViewId target, in byte data) => { });
                if (previous != default) edit.OrderAfter(pass, previous);
                previous = pass;
            }
            GraphBufferId output = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(new BufferRange(0, 256), result.InitialSync,
                    result.InitialAccess, ResourceContentState.Undefined)]);
            GraphPassId copy = edit.AddCopyFramePass("read final attachment", PassQueueSelection.Exact(support.GraphicsQueue),
                (Texture: texture, Buffer: output), new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Texture, range, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                    pass.Write(data.Buffer, new BufferRange(0, 256), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Partial);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                    commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0, 256, 1,
                        commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1)));
            edit.OrderAfter(copy, previous);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            using (RenderGraphFrame frame = graph.BeginFrame()) frame.Execute(completions);
            support.Wait(completions);
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, new BufferRange(0, 256));
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, mapping.Bytes[..4].ToArray());
        }
    }

    [Fact]
    public void LayeredMipTransfersPreserveDifferentProducerStatesAndReadBackEveryTexel()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.CopyQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        const uint layers = 32, mips = 3, cellBytes = 4096;
        const uint byteCount = layers * mips * cellBytes;
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(byteCount, BufferUsages.CopyDestination), MemoryType.Readback);
        TextureSubresourceRange whole = new(0, mips, 0, layers, TextureAspects.Color);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D, 16, 16, 1, mips, layers, 1, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.Sampled | TextureUsages.CopySource | TextureUsages.Storage));
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(new BufferRange(0, byteCount), result.InitialSync,
                    result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddCopyFramePass("clear all layers and mips", PassQueueSelection.Exact(support.GraphicsQueue),
                texture, new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref GraphTextureId data) => pass.Write(data, whole,
                    PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget,
                    WriteCoverage.Complete),
                (ref CopyPassCommandScope commands, in GraphTextureId data) =>
                    commands.ClearTexture(commands.GetTexture(data), whole, new Vector4(1, 0.5f, 0, 1)));
            GraphPassId middleMip = edit.AddGeneralFramePass("sample middle mip",
                PassQueueSelection.Exact(support.GraphicsQueue), texture,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphTextureId data) => pass.Read(data,
                    new TextureSubresourceRange(1, 1, 0, layers, TextureAspects.Color),
                    PipelineSync.ComputeShading, ResourceAccess.ShaderResource, TextureLayout.ShaderResource),
                static (ref GeneralPassCommandScope commands, in GraphTextureId data) => { });
            GraphPassId partial = edit.AddGeneralFramePass("partially update overlapping mip ranges",
                PassQueueSelection.Exact(support.GraphicsQueue), texture, new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref GraphTextureId data) =>
                {
                    pass.Write(data, whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, TextureLayout.UnorderedAccess, WriteCoverage.Partial);
                    pass.Write(data, new TextureSubresourceRange(1, 1, 0, layers, TextureAspects.Color),
                        PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, TextureLayout.UnorderedAccess, WriteCoverage.Partial);
                }, static (ref GeneralPassCommandScope commands, in GraphTextureId data) => { });
            edit.OrderAfter(partial, middleMip);
            var copyData = (Texture: texture, Buffer: destination);
            GraphPassId copy = edit.AddCopyFramePass("read every layer and mip",
                PassQueueSelection.Exact(support.CopyQueue), copyData,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Texture, whole, PipelineSync.Copy, ResourceAccess.CopySource,
                        TextureLayout.CopySource);
                    pass.Write(data.Buffer, new BufferRange(0, byteCount), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Partial);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    for (uint layer = 0; layer < layers; layer++)
                    for (uint mip = 0; mip < mips; mip++)
                    {
                        uint size = 16u >> checked((int)mip);
                        commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer),
                            (layer * mips + mip) * cellBytes, 256, size, commands.GetTexture(data.Texture),
                            mip, layer, TextureAspects.Color, 0, 0, 0, size, size, 1));
                    }
                });
            edit.OrderAfter(copy, partial);
            edit.Commit();
        }
        int barriers = 0;
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics: Capture)))
                frame.Execute(completions);
            support.Wait(completions);
            Assert.InRange(barriers, 1, 24);
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, new BufferRange(0, byteCount));
            for (uint layer = 0; layer < layers; layer++)
            for (uint mip = 0; mip < mips; mip++)
            {
                int size = 16 >> checked((int)mip);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int offset = checked((int)((layer * mips + mip) * cellBytes)) + y * 256 + x * 4;
                    Assert.Equal(255, mapping.Bytes[offset]);
                    Assert.InRange(mapping.Bytes[offset + 1], (byte)127, (byte)128);
                    Assert.Equal(0, mapping.Bytes[offset + 2]);
                    Assert.Equal(255, mapping.Bytes[offset + 3]);
                }
            }
        }
        void Capture(in RenderGraphDiagnosticsView diagnostics) => barriers = diagnostics.Statistics.BarrierCount;
    }
}
