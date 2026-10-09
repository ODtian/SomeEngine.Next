using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RepeatedCommandSequenceTests
{
    [Fact]
    public void RepeatedAttachmentClearKeepsItsRecordedCommandsBesideAFrameWrite()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(260, BufferUsages.CopyDestination), MemoryType.Readback);
        int clearRecords = 0;
        GraphBufferId destination;
        GraphPassId changing;
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
            GraphPassId clear = edit.AddCopyFramePass("clear repeated attachment", PassQueueSelection.AnyOfType(QueueType.Graphics),
                texture, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                (ref PassDefinition pass, ref GraphTextureId data) => pass.Write(data, range,
                    PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete),
                (ref CopyPassCommandScope commands, in GraphTextureId data) =>
                {
                    clearRecords++;
                    commands.ClearTexture(commands.GetTexture(data), range, new(1, 0, 0, 1));
                });
            changing = edit.AddCopyFramePass("write frame word", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new BufferWord(destination, 256, 11), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref BufferWord data) => pass.Write(data.Buffer,
                    new BufferRange(data.Offset, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in BufferWord data) =>
                    commands.ClearBuffer(commands.GetBuffer(data.Buffer), new BufferRange(data.Offset, 4), data.Value));
            edit.OrderAfter(changing, clear);
            edit.AddCopyFramePass("read attachment", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (texture, destination), new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Texture, range, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                    pass.Write(data.Buffer, new BufferRange(0, 256), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Partial);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                    commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0, 256, 1,
                        commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1)));
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (uint value = 11; value <= 12; value++) Execute(value);
        int recordings = clearRecords;
        Execute(13);
        Assert.Equal(recordings, clearRecords);
        void Execute(uint value)
        {
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                frame.SetPassDataIfChanged(changing, new BufferWord(destination, 256, value));
                frame.Execute(completions);
            }
            support.Wait(completions);
            using MappedBuffer mapping = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, mapping.Bytes[..4].ToArray());
            Assert.Equal(value, MemoryMarshal.Read<uint>(mapping.Bytes[256..]));
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.Pipelined)]
    public void ReusedCommandsObserveLocalDataChangesAndSurroundingWrites(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        const int count = 48;
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(count * sizeof(uint), BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId destination;
        var passes = new GraphPassId[count];
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            for (int index = 0; index < count; index++)
            {
                var value = new BufferWord(destination, (ulong)index * sizeof(uint), (uint)index + 1);
                passes[index] = edit.AddCopyFramePass("write output element", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    value, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: index != 0 && index != count - 1),
                    static (ref PassDefinition pass, ref BufferWord data) => pass.Write(data.Buffer,
                        new BufferRange(data.Offset, sizeof(uint)), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
                    static (ref CopyPassCommandScope commands, in BufferWord data) =>
                        commands.ClearBuffer(commands.GetBuffer(data.Buffer), new BufferRange(data.Offset, sizeof(uint)), data.Value));
                if (index != 0) edit.OrderAfter(passes[index], passes[index - 1]);
            }
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 12; frameIndex++)
        {
            uint changed = frameIndex < 4 ? 18u : frameIndex < 8 ? 1001u : 2002u;
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(SubmissionMode: mode)))
            {
                frame.SetPassDataIfChanged(passes[17], new BufferWord(destination, 17 * sizeof(uint), changed));
                frame.SetPassData(passes[0], new BufferWord(destination, 0, (uint)frameIndex + 100));
                frame.SetPassData(passes[^1], new BufferWord(destination, (count - 1) * sizeof(uint), (uint)frameIndex + 200));
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer readback = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
            readback.Invalidate(readback.Range);
            ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(readback.Bytes);
            for (int index = 0; index < count; index++)
            {
                uint expected = index == 0 ? (uint)frameIndex + 100 : index == count - 1 ? (uint)frameIndex + 200 :
                    index == 17 ? changed : (uint)index + 1;
                Assert.Equal(expected, values[index]);
            }
        }
    }

    private readonly record struct BufferWord(GraphBufferId Buffer, ulong Offset, uint Value);
}
