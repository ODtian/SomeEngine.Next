using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class AtomicTextureFrameTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MixedShaderStagesPreserveTextureContentsAcrossFrames(bool partialRange, bool samePass)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(1024, BufferUsages.CopyDestination), MemoryType.Readback);
        TextureSubresourceRange whole = new(0, 1, 0, 2, TextureAspects.Color);
        TextureSubresourceRange pixelRange = partialRange ? new(0, 1, 1, 1, TextureAspects.Color) : whole;
        GraphPassId clear, readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D, 1, 1, 1, 1, 2, 1, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.Storage | TextureUsages.CopySource));
            GraphBufferId destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            clear = edit.AddCopyFramePass("initialize texture contents", PassQueueSelection.AnyOfType(QueueType.Graphics),
                texture, new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref GraphTextureId data) => pass.Write(data, whole,
                    PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete),
                (ref CopyPassCommandScope commands, in GraphTextureId data) =>
                    commands.ClearTexture(commands.GetTexture(data), whole, new Vector4(0.25f, 0.5f, 0.75f, 1)));
            // Potential writes still contribute to synchronization when a callback has no work.
            GraphPassId compute = edit.AddComputeFramePass("compute stage access", PassQueueSelection.AnyOfType(QueueType.Graphics),
                texture, new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref GraphTextureId data) =>
                {
                    pass.ReadWrite(data, whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess,
                        TextureLayout.UnorderedAccess, unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
                    if (samePass)
                        pass.ReadWrite(data, pixelRange, PipelineSync.PixelShading, ResourceAccess.UnorderedAccess,
                            TextureLayout.UnorderedAccess, unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
                }, static (ref ComputePassCommandScope commands, in GraphTextureId data) => { });
            edit.OrderAfter(compute, clear);
            GraphPassId last = compute;
            if (!samePass)
            {
                last = edit.AddComputeFramePass("pixel stage access", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    texture, new PassOptions(Culling: PassCullingMode.NeverCull),
                    (ref PassDefinition pass, ref GraphTextureId data) => pass.ReadWrite(data, pixelRange,
                        PipelineSync.PixelShading, ResourceAccess.UnorderedAccess, TextureLayout.UnorderedAccess,
                        unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable),
                    static (ref ComputePassCommandScope commands, in GraphTextureId data) => { });
                edit.OrderAfter(last, compute);
            }
            readback = edit.AddCopyFramePass("read preserved contents", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (texture, destination), new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Texture, whole, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                    pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Partial);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    for (uint layer = 0; layer < 2; layer++)
                        commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), layer * 512u,
                            256, 1, commands.GetTexture(data.Texture), 0, layer, TextureAspects.Color, 0, 0, 0, 1, 1, 1));
                });
            edit.OrderAfter(readback, last);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 6; index++)
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.SetPassEnabled(clear, index == 0);
            frame.SetPassEnabled(readback, index == 5);
            frame.Execute(completions);
            support.Wait(completions);
        }
        using MappedBuffer mapped = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
        for (int layer = 0; layer < 2; layer++)
        {
            ReadOnlySpan<byte> pixel = mapped.Bytes.Slice(layer * 512, 4);
            Assert.InRange(pixel[0], (byte)63, (byte)64);
            Assert.InRange(pixel[1], (byte)127, (byte)128);
            Assert.InRange(pixel[2], (byte)191, (byte)192);
            Assert.Equal(255, pixel[3]);
        }
    }
}
