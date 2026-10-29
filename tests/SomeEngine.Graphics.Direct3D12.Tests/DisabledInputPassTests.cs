using SomeEngine.Graphics.Validation;
using SomeEngine.RenderGraph;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class DisabledInputPassTests
{
    [Fact]
    public void ARecreatedUploadPassPreservesItsResultWhileDisabledAndUsesTheNextEnabledInput()
    {
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using var graph = new SomeEngine.RenderGraph.RenderGraph(backend, device, [backend.GetQueue(device, QueueType.Graphics)],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer result = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphBufferId data, output;
        GraphPassId upload, read;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            data = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            output = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            upload = AddUpload(edit, input, data);
            read = edit.AddCopyFramePass("read retained result", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (data, output), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Output) value) =>
                {
                    pass.Read(value.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(value.Output, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Output) value) =>
                    commands.CopyBuffer(new(commands.GetBuffer(value.Source), 0, commands.GetBuffer(value.Output), 0, 4)));
            edit.OrderAfter(read, upload); edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferInput(input, frame.Upload(new byte[] { 1, 2, 3, 4 }, BufferUsages.CopySource));
            frame.Execute(completions);
        }
        Wait(backend, completions);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(upload); upload = AddUpload(edit, input, data); edit.OrderAfter(read, upload); edit.Commit();
        }

        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassEnabled(upload, false);
            frame.Execute(completions);
        }
        Wait(backend, completions);
        using (MappedBuffer mapping = backend.Map(result, MapType.Read, BufferRange.Whole))
        {
            mapping.Invalidate(mapping.Range);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, mapping.Bytes.ToArray());
        }
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferInput(input, frame.Upload(new byte[] { 5, 6, 7, 8 }, BufferUsages.CopySource));
            frame.SetPassEnabled(upload, true); frame.Execute(completions);
        }
        Wait(backend, completions);
        using MappedBuffer final = backend.Map(result, MapType.Read, BufferRange.Whole);
        final.Invalidate(final.Range);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, final.Bytes.ToArray());
    }

    private static void Wait(IGraphicsBackend backend, ReadOnlySpan<QueueCompletion> completions)
    {
        foreach (QueueCompletion completion in completions)
            if (completion != default) Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
    }

    private static GraphPassId AddUpload(RenderGraphEdit edit, GraphBufferInput input, GraphBufferId data) =>
        edit.AddCopyFramePass("upload changed data", PassQueueSelection.AnyOfType(QueueType.Graphics), (input, data),
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Data) value) =>
            {
                pass.Read(value.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(value.Data, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Data) value) =>
            {
                BufferRegion source = commands.GetBufferRegion(value.Input);
                commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(value.Data), 0, 4));
            });
}
