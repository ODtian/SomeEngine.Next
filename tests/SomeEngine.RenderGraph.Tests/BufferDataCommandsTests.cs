using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class BufferDataCommandsTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void WrittenUploadsPreserveBytesAndPaddingAcrossFailedWritesAndCanceledFrames(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 3));
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 9; index++)
        {
            byte[] bytes = Enumerable.Range(0, 29).Select(value => (byte)(value + index * 17)).ToArray();
            byte[] expected = [.. bytes.Reverse(), 0, 0, 0];
            using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
            {
                if (index == 2)
                {
                    bool failed = false;
                    try
                    {
                        _ = frame.Upload(17, BufferUsages.CopySource, 0,
                            static (destination, _) =>
                            {
                                destination.Fill(255);
                                throw new FormatException("Input generation failed.");
                            });
                    }
                    catch (FormatException) { failed = true; }
                    Assert.True(failed);
                }
                GraphBufferSlice source = frame.Upload(bytes.Length, BufferUsages.Constant, bytes,
                    static (destination, data) =>
                    {
                        for (int position = 0; position < data.Length; position++)
                            destination[position] = data[data.Length - 1 - position];
                    });
                bytes.AsSpan().Fill(255);
                GraphBufferId destination = frame.Import(result,
                    [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
                frame.DeclareBufferCopy("copy written upload", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    source with { Range = new BufferRange(source.Range.Offset, 32) },
                    new GraphBufferSlice(destination, new BufferRange(0, 32)),
                    new PassOptions(Culling: PassCullingMode.NeverCull));
                if (index == 3) continue;
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(expected, mapping.Bytes.ToArray());
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void ImmediatePassAccessesCanBeDeclaredAfterCreation(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 4; index++)
        {
            byte[] expected = Enumerable.Range(0, 32).Select(value => (byte)(value + index * 17)).ToArray();
            using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
            {
                GraphBufferSlice source = frame.Upload(expected, BufferUsages.CopySource);
                GraphBufferId destination = frame.Import(result,
                    [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
                GraphPassId copy = frame.AddCopyPass("copy with later access declarations",
                    PassQueueSelection.AnyOfType(QueueType.Graphics), (Source: source, Destination: destination),
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition _, ref (GraphBufferSlice Source, GraphBufferId Destination) _) => { },
                    static (ref CopyPassCommandScope commands, in (GraphBufferSlice Source, GraphBufferId Destination) data) =>
                        commands.CopyBuffer(new(commands.GetBuffer(data.Source.Buffer), data.Source.Range.Offset,
                            commands.GetBuffer(data.Destination), 0, 32)));
                PassDefinition access = frame.GetPass(copy);
                access.Read(source.Buffer, source.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                access.Write(destination, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
                if (index == 1) continue;
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(expected, mapping.Bytes.ToArray());
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void SegmentedUploadsPreserveBytesAcrossFramesAndCancellation(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 3));
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddCopyFramePass("copy segmented upload", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new CopyData(input, destination, 0), new PassOptions(RepeatCommands: true),
                static (ref PassDefinition pass, ref CopyData data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyData data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Source);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset,
                        commands.GetBuffer(data.Destination), 0, 32));
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 9; index++)
        {
            byte[] expected = Enumerable.Range(0, 32).Select(value => (byte)(value + index * 17)).ToArray();
            byte[] bytes = expected.ToArray();
            ReadOnlyMemory<byte>[] segments = [bytes.AsMemory(0, 7), ReadOnlyMemory<byte>.Empty,
                bytes.AsMemory(7, 13), bytes.AsMemory(20, 12)];
            using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
            {
                _ = frame.Upload(new byte[28], BufferUsages.CopySource);
                frame.SetBufferInput(input, frame.Upload(segments, BufferUsages.CopySource));
                bytes.AsSpan().Fill(255);
                if (index == 3) continue;
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(expected, mapping.Bytes.ToArray());
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    [InlineData(FrameSubmissionMode.Pipelined, true)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit, true)]
    [InlineData(FrameSubmissionMode.RecordAhead, true)]
    public void BufferDataAndCommandEditsReachEveryFrame(FrameSubmissionMode mode, bool mapped = false)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: false);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 3));
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId source, destination;
        GraphBufferInput input;
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            source = edit.DeclareExternalBuffer(new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
            destination = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(BufferRange.Whole, result.InitialSync, result.InitialAccess,
                    ResourceContentState.Undefined)]);
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            pass = edit.AddCopyFramePass("copy changing data", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new CopyData(input, destination, 0), new PassOptions(RepeatCommands: true),
                static (ref PassDefinition definition, ref CopyData data) =>
                {
                    definition.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    definition.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyData data) =>
                {
                    BufferRegion input = commands.GetBufferRegion(data.Source);
                    commands.CopyBuffer(new BufferCopy(input.Buffer, input.Range.Offset + data.Offset,
                        commands.GetBuffer(data.Destination), 0, 32));
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 16; index++)
        {
            if (index == 8)
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.SetBufferDescription(source, new BufferDesc(96, BufferUsages.CopySource), MemoryType.Upload);
                edit.Commit();
            }
            byte[] bytes = Enumerable.Range(0, index < 8 ? 64 : 96)
                .Select(value => unchecked((byte)(index * 13 + value))).ToArray();
            ulong offset = index < 5 ? 0u : 16u;
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(SubmissionMode: mode)))
            {
                if (mapped) bytes.CopyTo(frame.GetBufferDataWriteSpan(source));
                else frame.SetBufferData(source, bytes);
                frame.SetBufferInput(input, new GraphBufferSlice(source, BufferRange.Whole));
                frame.SetPassDataIfChanged(pass, new CopyData(input, destination, offset));
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.True(mapping.Bytes.SequenceEqual(bytes.AsSpan((int)offset, 32)));
        }
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(pass);
            edit.Remove(source);
            edit.Commit();
        }
        using RenderGraphFrame last = graph.BeginFrame();
        last.Execute(completions);
        support.Wait(completions);
    }

    private readonly record struct CopyData(GraphBufferInput Source, GraphBufferId Destination, ulong Offset);
}
