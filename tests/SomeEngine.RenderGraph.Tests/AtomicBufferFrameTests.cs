using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using Xunit.Sdk;

namespace SomeEngine.RenderGraph.Tests;

public sealed class AtomicBufferFrameTests
{
    [HardwareRowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisjointAtomicWritesPreserveWholeBufferSynchronization(bool vulkan)
    {
        if (!vulkan && !OperatingSystem.IsWindows())
            throw SkipException.ForSkip("D3D12 WARP requires Windows.");
        using IGraphicsBackend backend = new ValidationLayer(vulkan
            ? VulkanGraphicsBackend.Create() : D3D12GraphicsBackend.Create(),
            new ValidationOptions(NativeValidationDisabled: vulkan));
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(enumeration, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(enumeration, adapters, out _);
        AdapterInfo adapter = vulkan ? adapters[0] : adapters.First(value => !value.HardwareAccelerated);
        using Device device = backend.CreateDevice(new DeviceDesc(adapter.Id, [new(QueueType.Graphics)]));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using var graph = new RenderGraph(backend, device, [queue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer upload = backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer output = backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopyDestination), MemoryType.Readback);
        byte[] expected = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            expected.CopyTo(mapped.Bytes);
            mapped.Flush(mapped.Range);
        }
        GraphPassId initialize, readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId source = edit.RegisterExternalBuffer(upload,
                [new(BufferRange.Whole, upload.InitialSync, upload.InitialAccess, ResourceContentState.Defined)]);
            GraphBufferId result = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopyDestination | BufferUsages.CopySource | BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
            initialize = edit.AddCopyFramePass("initialize shared data", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (Source: source, Destination: buffer), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0, commands.GetBuffer(data.Destination), 0, 16)));
            GraphPassId previous = initialize;
            for (int index = 0; index < 2; index++)
            {
                var data = (Buffer: buffer, Range: new BufferRange((ulong)index * 8, 8),
                    Sync: index == 0 ? PipelineSync.ComputeShading : PipelineSync.PixelShading);
                // A pass may have no active work; its declared access still establishes the boundary state.
                GraphPassId pass = edit.AddComputeFramePass("atomic data access", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    data, new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition access, ref (GraphBufferId Buffer, BufferRange Range, PipelineSync Sync) value) =>
                        access.ReadWrite(value.Buffer, value.Range, value.Sync, ResourceAccess.UnorderedAccess,
                            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable),
                    static (ref ComputePassCommandScope commands, in (GraphBufferId Buffer, BufferRange Range, PipelineSync Sync) value) => { });
                edit.OrderAfter(pass, previous);
                previous = pass;
            }
            readback = edit.AddCopyFramePass("read shared data", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (Source: buffer, Destination: result), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0, commands.GetBuffer(data.Destination), 0, 16)));
            edit.OrderAfter(readback, previous);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 6; index++)
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.SetPassEnabled(initialize, index == 0);
            frame.SetPassEnabled(readback, index == 5);
            frame.Execute(completions);
            foreach (QueueCompletion completion in completions)
                if (completion != default) Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
        }
        using MappedBuffer readbackBytes = backend.Map(output, MapType.Read, BufferRange.Whole);
        readbackBytes.Invalidate(readbackBytes.Range);
        Assert.True(readbackBytes.Bytes.SequenceEqual(expected));
        backend.CollectCompleted(device);
    }
}
