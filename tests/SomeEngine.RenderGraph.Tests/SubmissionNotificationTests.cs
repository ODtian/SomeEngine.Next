using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class SubmissionNotificationTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void EarlyCopyCanBeReadBeforeLaterRecordingFailsAndAfterGraphEdits(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphPassId first;
        bool fail = true, laterRecorded = false;
        var options = new PassOptions(Culling: PassCullingMode.NeverCull,
            Recording: PassRecordingMode.CallingThread) { SubmitAfter = true };
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            first = edit.AddCopyFramePass("early output", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (Source: input, Destination: destination), options,
                static (ref PassDefinition access, ref (GraphBufferInput Source, GraphBufferId Destination) data) =>
                {
                    access.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    access.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Source, GraphBufferId Destination) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Source);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset,
                        commands.GetBuffer(data.Destination), 0, 32));
                });
            GraphPassId later = edit.AddGeneralFramePass("later work", PassQueueSelection.AnyOfType(QueueType.Graphics),
                0, options with { SubmitAfter = false },
                static (ref PassDefinition _, ref int _) => { },
                (ref GeneralPassCommandScope _, in int _) =>
                {
                    laterRecorded = true;
                    if (fail) throw new InvalidOperationException("later recording failed");
                });
            edit.OrderAfter(later, first);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            bool early = iteration % 2 == 0;
            if (iteration < 2)
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.SetPassOptions(first, options with { SubmitAfter = early });
                edit.Commit();
            }
            fail = early;
            laterRecorded = false;
            byte[] expected = Enumerable.Range(0, 32).Select(value => (byte)(value + iteration * 23)).ToArray();
            bool observedBeforeLater = false;
            var observer = new Receiver(result =>
            {
                if (!result.TryGetPassCompletion(first, out QueueCompletion completion)) return;
                observedBeforeLater = !laterRecorded;
                support.Wait([completion]);
                using MappedBuffer mapping = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
                mapping.Invalidate(mapping.Range);
                Assert.Equal(expected, mapping.Bytes.ToArray());
            });
            using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
            {
                if (iteration >= 2) frame.SetPassOptions(first, options with { SubmitAfter = early });
                frame.SetBufferInput(input, frame.Upload(expected, BufferUsages.CopySource));
                frame.ObserveExecution(observer);
                if (fail)
                {
                    bool threw = false;
                    try { frame.Execute(completions); }
                    catch (Exception exception)
                    {
                        Assert.Contains("later recording failed", exception.ToString());
                        threw = true;
                    }
                    Assert.True(threw);
                }
                else frame.Execute(completions);
            }
            Assert.Equal(early, observedBeforeLater);
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void TextureRemainsUsableAfterRecordingFailsBeyondAnAcceptedSubmission(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(256, BufferUsages.CopyDestination), MemoryType.Readback);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        bool fail = true;
        var options = new PassOptions(Culling: PassCullingMode.NeverCull,
            Recording: PassRecordingMode.CallingThread);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
            GraphPassId clear = edit.AddCopyFramePass("accepted texture clear",
                PassQueueSelection.Exact(support.GraphicsQueue), texture,
                options with { SubmitAfter = true },
                (ref PassDefinition access, ref GraphTextureId data) => access.Write(data, range,
                    PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget,
                    WriteCoverage.Complete),
                (ref CopyPassCommandScope commands, in GraphTextureId data) =>
                    commands.ClearTexture(commands.GetTexture(data), range, new System.Numerics.Vector4(0, 1, 0, 1)));
            GraphPassId later = edit.AddGeneralFramePass("interrupt later texture recording",
                PassQueueSelection.Exact(support.GraphicsQueue), 0,
                options with { EstimatedExecutionCost = 8 },
                static (ref PassDefinition _, ref int _) => { },
                (ref GeneralPassCommandScope _, in int _) => { if (fail) throw new InjectedRecordingFailure(); });
            GraphBufferId destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            GraphPassId copy = edit.AddCopyFramePass("read recovered texture",
                PassQueueSelection.Exact(support.GraphicsQueue), (Texture: texture, Buffer: destination), options,
                (ref PassDefinition access, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    access.Read(data.Texture, range, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                    access.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Partial);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                    commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0, 256, 1,
                        commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1)));
            edit.OrderAfter(later, clear);
            edit.OrderAfter(copy, later);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        bool accepted = false;
        var observer = new Receiver(result =>
        {
            accepted |= !result.SubmittedCompletions.IsEmpty;
            support.Wait(result.SubmittedCompletions);
        });
        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
        {
            frame.ObserveExecution(observer);
            bool rejected = false;
            try { frame.Execute(completions); }
            catch (InjectedRecordingFailure) { rejected = true; }
            Assert.True(rejected);
        }
        Assert.True(accepted);
        fail = false;
        for (int iteration = 0; iteration < 2; iteration++)
        {
            using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
                frame.Execute(completions);
            support.Wait(completions);
            using MappedBuffer mapping = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, mapping.Bytes[..4].ToArray());
        }
    }

    private sealed class InjectedRecordingFailure : Exception;

    private delegate void SubmissionReceived(RenderGraphFrameResult result);
    private sealed class Receiver(SubmissionReceived receive) : IRenderGraphFrameExecutionObserver
    {
        public void OnRenderGraphSubmitted(RenderGraphFrameResult result) => receive(result);
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) { }
    }
}
