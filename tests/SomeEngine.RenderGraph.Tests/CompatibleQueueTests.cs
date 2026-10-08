using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CompatibleQueueTests
{
    [Fact]
    public void CompatibleCopiesPreferTheirOnlyConsumerQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue, support.CopyQueue]);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphPassId copy;
        GraphPassId consumer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId other = edit.CreateBuffer(new(32, BufferUsages.CopyDestination));
            GraphBufferId buffer = edit.CreateBuffer(new(32, BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddCopyFramePass("independent graphics work", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (input, other), new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedExecutionCost: 64),
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(data.Buffer), 0, 32));
                });
            copy = edit.AddCopyFramePass("copy for graphics", PassQueueSelection.AnyCompatible(),
                (input, buffer), default,
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(data.Buffer), 0, 32));
                });
            consumer = edit.AddCopyFramePass("consume on graphics", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (buffer, destination), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0, commands.GetBuffer(data.Destination), 0, 32)));
            edit.Commit();
        }
        byte[] data = Enumerable.Range(0, 32).Select(index => (byte)(index * 7)).ToArray();
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        Queue? copyQueue = null;
        using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: CaptureQueue)))
        {
            frame.SetBufferInput(input, frame.Upload(data, BufferUsages.CopySource));
            frame.Execute(completions);
            support.Wait(completions);
            Assert.Same(support.GraphicsQueue, copyQueue);
        }
        using (MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole))
        {
            mapping.Invalidate(mapping.Range);
            Assert.Equal(data, mapping.Bytes.ToArray());
        }
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetPassQueue(consumer, PassQueueSelection.AnyOfType(QueueType.Copy));
            edit.Commit();
        }
        byte[] next = Enumerable.Range(0, 32).Select(index => (byte)(255 - index * 3)).ToArray();
        using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: CaptureQueue)))
        {
            frame.SetBufferInput(input, frame.Upload(next, BufferUsages.CopySource));
            frame.Execute(completions);
            support.Wait(completions);
            Assert.Same(support.CopyQueue, copyQueue);
        }
        using (MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole))
        {
            mapping.Invalidate(mapping.Range);
            Assert.Equal(next, mapping.Bytes.ToArray());
        }

        void CaptureQueue(in RenderGraphDiagnosticsView diagnostics)
        {
            foreach (RenderGraphPassDiagnostic pass in diagnostics.Passes)
                if (pass.Id == copy) copyQueue = pass.Queue;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompatibleCopiesPreferTheirProducerAndExplicitQueuesAreRespected(bool dedicatedCopyQueue)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue, support.CopyQueue]);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphPassId copy;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId buffer = edit.CreateBuffer(new(32, BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddCopyFramePass("produce on graphics", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (input, buffer), default,
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(data.Buffer), 0, 32));
                });
            copy = edit.AddCopyFramePass("consume produced buffer", dedicatedCopyQueue
                    ? PassQueueSelection.AnyOfType(QueueType.Copy) : PassQueueSelection.AnyCompatible(),
                (buffer, destination), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0, commands.GetBuffer(data.Destination), 0, 32)));
            edit.Commit();
        }
        byte[] data = Enumerable.Range(0, 32).Select(index => (byte)(index * 7)).ToArray();
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        Queue? copyQueue = null;
        using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: CaptureQueue)))
        {
            frame.SetBufferInput(input, frame.Upload(data, BufferUsages.CopySource));
            frame.Execute(completions);
            Assert.Same(dedicatedCopyQueue ? support.CopyQueue : support.GraphicsQueue, copyQueue);
            support.Wait(completions);
        }
        using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        Assert.Equal(data, mapping.Bytes.ToArray());

        void CaptureQueue(in RenderGraphDiagnosticsView diagnostics)
        {
            foreach (RenderGraphPassDiagnostic pass in diagnostics.Passes)
                if (pass.Id == copy) copyQueue = pass.Queue;
        }
    }

    [Theory]
    [InlineData(QueueType.Graphics)]
    [InlineData(QueueType.Compute)]
    [InlineData(QueueType.Copy)]
    public void CopiesRunOnAnyRegisteredCompatibleQueue(QueueType type)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        Queue queue = type switch
        {
            QueueType.Graphics => support.GraphicsQueue,
            QueueType.Compute => support.ComputeQueue,
            _ => support.CopyQueue,
        };
        using var graph = new RenderGraph(support.Backend, support.Device, [queue]);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddCopyFramePass("copy on compatible queue", PassQueueSelection.AnyCompatible(),
                (input, destination), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Destination) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset,
                        commands.GetBuffer(data.Destination), 0, 32));
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 12; frameIndex++)
        {
            byte[] data = Enumerable.Range(0, 32).Select(index => (byte)(index * 3 + frameIndex)).ToArray();
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                frame.SetBufferInput(input, frame.Upload(data, BufferUsages.CopySource));
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(data, mapping.Bytes.ToArray());
        }
    }

    [Fact]
    public void GeneralCommandsStillRequireAGraphicsQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.ComputeQueue]);
        using RenderGraphEdit edit = graph.BeginEdit();
        bool rejected = false;
        try
        {
            edit.DeclareGeneralPass("general commands", PassQueueSelection.AnyCompatible(), options: new PassOptions(Culling: PassCullingMode.NeverCull));
            edit.Commit();
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Assert.True(rejected);
    }
}
