using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CountedBufferRangesTests
{
    [Fact]
    public void BoundedPrefixReadbackRecoversAfterInterruptedRecording()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer readback = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphBufferId payload;
        bool interrupt = true;
        var options = new PassOptions(Culling: PassCullingMode.NeverCull,
            Recording: PassRecordingMode.CallingThread);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            payload = edit.CreatePersistentBuffer(new BufferDesc(64,
                BufferUsages.ShaderWrite | BufferUsages.CopyDestination | BufferUsages.CopySource));
            GraphBufferId destination = edit.RegisterExternalBuffer(readback,
                [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            GraphBufferUavId descriptor = edit.CreateBufferUav(payload, structureStride: sizeof(uint));
            GraphPassId main = edit.AddGeneralFramePass("bind the queue descriptor",
                PassQueueSelection.Exact(support.GraphicsQueue), descriptor, options,
                static (ref PassDefinition pass, ref GraphBufferUavId view) =>
                    pass.Bind(GraphParameterResourceBinding.WritableBufferDescriptor(view)),
                static (ref GeneralPassCommandScope _, in GraphBufferUavId _) => { });
            GraphPassId produce = edit.AddCopyFramePass("write the current prefix",
                PassQueueSelection.Exact(support.GraphicsQueue), (Input: input, Payload: payload),
                options with { SubmitAfter = true },
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Payload) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Payload, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Partial, ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedPrefix);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Payload) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(data.Payload), 0, 4));
                });
            GraphPassId interruption = edit.AddGeneralFramePass("interrupt the consumer recording",
                PassQueueSelection.Exact(support.GraphicsQueue), 0, options,
                static (ref PassDefinition _, ref int _) => { },
                (ref GeneralPassCommandScope _, in int _) =>
                {
                    if (interrupt) throw new PrefixRecordingFailure();
                });
            GraphPassId consume = edit.AddCopyFramePass("read the produced prefix",
                PassQueueSelection.Exact(support.GraphicsQueue), (Payload: payload, Output: destination), options,
                static (ref PassDefinition pass, ref (GraphBufferId Payload, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Payload, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource,
                        contentScope: BufferContentScope.CountBoundedPrefix);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Payload, GraphBufferId Output) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Payload), 0, commands.GetBuffer(data.Output), 0, 4)));
            edit.OrderAfter(produce, main);
            edit.OrderAfter(interruption, produce);
            edit.OrderAfter(consume, interruption);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferInput(input, frame.Upload(new byte[] { 99, 98, 97, 96 }, BufferUsages.CopySource));
            PrefixRecordingFailure? failure = null;
            try { frame.Execute(completions); }
            catch (PrefixRecordingFailure error) { failure = error; }
            Assert.NotNull(failure);
        }
        QueueCompletion[] accepted = graph.GetRangeCompletions(payload, BufferRange.Whole);
        Assert.NotEmpty(accepted);
        support.Wait(accepted);

        interrupt = false;
        byte[] expected = [1, 2, 3, 4];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferInput(input, frame.Upload(expected, BufferUsages.CopySource));
            frame.Execute(completions);
            support.Wait(completions);
        }
        using MappedBuffer mapped = support.Backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(expected, mapped.Bytes.ToArray());
    }

    private sealed class PrefixRecordingFailure : Exception;

    [Theory]
    [InlineData(BufferContentScope.CountBoundedRanges, true)]
    [InlineData(BufferContentScope.CountBoundedPrefix, false)]
    [InlineData(BufferContentScope.FullRange, false)]
    public void DisjointRangesRequireReadersToRespectTheirBounds(BufferContentScope scope, bool accepted)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer upload = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8];
        using (MappedBuffer mapped = support.Backend.Map(upload, MapType.Write, BufferRange.Whole))
            expected.CopyTo(mapped.Bytes);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId source = edit.RegisterExternalBuffer(upload,
                [new(BufferRange.Whole, upload.InitialSync, upload.InitialAccess, ResourceContentState.Defined)]);
            GraphBufferId result = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            GraphBufferId ranges = edit.CreateTransientBuffer(new BufferDesc(32,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.DeclareCopyPass("write two bounded ranges", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (ref PassDefinition pass) =>
                {
                    pass.Read(source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(ranges, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Partial, resultContents: ResourceContentState.Defined,
                        contentScope: BufferContentScope.CountBoundedRanges);
                },
                (ref CopyPassCommandScope commands) =>
                {
                    commands.CopyBuffer(new(commands.GetBuffer(source), 0, commands.GetBuffer(ranges), 4, 4));
                    commands.CopyBuffer(new(commands.GetBuffer(source), 4, commands.GetBuffer(ranges), 24, 4));
                });
            edit.DeclareCopyPass("read only the produced elements", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (ref PassDefinition pass) =>
                {
                    pass.Read(ranges, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource, contentScope: scope);
                    pass.Write(result, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                (ref CopyPassCommandScope commands) =>
                {
                    commands.CopyBuffer(new(commands.GetBuffer(ranges), 4, commands.GetBuffer(result), 0, 4));
                    commands.CopyBuffer(new(commands.GetBuffer(ranges), 24, commands.GetBuffer(result), 4, 4));
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        UndefinedResourceReadException? failure = null;
        try { frame.Execute(completions); }
        catch (UndefinedResourceReadException error) { failure = error; }
        if (!accepted)
        {
            Assert.NotNull(failure);
            return;
        }
        Assert.Null(failure);
        support.Wait(completions);
        using MappedBuffer readback = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
        Assert.True(readback.Bytes.SequenceEqual(expected));
    }
}
