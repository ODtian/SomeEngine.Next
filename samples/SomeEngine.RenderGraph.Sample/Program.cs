using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.RenderGraph.Sample;

internal readonly record struct CopyState(
    GraphBufferId Source,
    GraphBufferId Destination,
    ulong Size);

internal static class Program
{
    private const int ByteCount = 256;

    public static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Direct3D 12 Render Graph sample requires Windows.");
            return 2;
        }

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        AdapterInfo warp = SelectWarp(backend);
        DeviceQueueDesc[] queueDescriptions =
        [
            new(QueueType.Graphics),
            new(QueueType.Copy),
        ];
        using Device device = backend.CreateDevice(new DeviceDesc(
            warp.Id,
            queueDescriptions,
            label: "Persistent Render Graph sample"));
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);

        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(ByteCount, BufferUsages.CopySource, "Sample upload"),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination, "Sample readback"),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            backend,
            device,
            [graphicsQueue, copyQueue],
            new RenderGraphDesc(Label: "Persistent copy graph"));

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId uploadId = edit.RegisterExternalBuffer(
                upload,
                [new BufferBoundaryState(
                    new BufferRange(0, ByteCount),
                    upload.InitialSync,
                    upload.InitialAccess,
                    ResourceContentState.Defined,
                    copyQueue)]);
            GraphBufferId readbackId = edit.RegisterExternalBuffer(
                readback,
                [new BufferBoundaryState(
                    new BufferRange(0, ByteCount),
                    readback.InitialSync,
                    readback.InitialAccess,
                    ResourceContentState.Undefined,
                    graphicsQueue)]);
            GraphBufferId transient = edit.CreateTransientBuffer(new BufferDesc(
                ByteCount,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "Sample transient"));

            _ = edit.AddCopyPass<CopyState, byte>(
                "Stage upload",
                PassQueueSelection.Exact(copyQueue),
                new CopyState(uploadId, transient, ByteCount),
                default,
                static (ref PassDefinition access, ref CopyState state) =>
                {
                    _ = access.Read(
                        state.Source,
                        new BufferRange(0, state.Size),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource);
                    _ = access.Write(
                        state.Destination,
                        new BufferRange(0, state.Size),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyState state, in byte _) =>
                {
                    Buffer source = commands.GetBuffer(state.Source);
                    Buffer destination = commands.GetBuffer(state.Destination);
                    commands.CopyBuffer(new BufferCopy(source, 0, destination, 0, state.Size));
                });

            _ = edit.AddCopyPass<CopyState, byte>(
                "Copy to readback",
                PassQueueSelection.Exact(graphicsQueue),
                new CopyState(transient, readbackId, ByteCount),
                default,
                static (ref PassDefinition access, ref CopyState state) =>
                {
                    _ = access.Read(
                        state.Source,
                        new BufferRange(0, state.Size),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource);
                    _ = access.Write(
                        state.Destination,
                        new BufferRange(0, state.Size),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyState state, in byte _) =>
                {
                    Buffer source = commands.GetBuffer(state.Source);
                    Buffer destination = commands.GetBuffer(state.Destination);
                    commands.CopyBuffer(new BufferCopy(source, 0, destination, 0, state.Size));
                });
            edit.Commit();
        }

        ExecuteFrame(graph, backend, upload, readback, seed: 17);
        ExecuteFrame(graph, backend, upload, readback, seed: 91);
        backend.CollectCompleted(device);
        Console.WriteLine("Executed two frames from one persistent Render Graph on D3D12 WARP.");
        return 0;
    }

    private static void ExecuteFrame(
        RenderGraph graph,
        IGraphicsBackend backend,
        Buffer upload,
        Buffer readback,
        byte seed)
    {
        byte[] expected = CreateInput(seed);
        BufferRange range = new(0, ByteCount);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, range))
        {
            expected.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
            _ = frame.Execute(completions);

        foreach (ref readonly QueueCompletion completion in completions.AsSpan())
        {
            Queue? queue;
            try { queue = completion.Queue; }
            catch { continue; }
            if (backend.WaitCpu(completion, TimeSpan.FromSeconds(10)) != WaitStatus.Completed)
                throw new TimeoutException("The Render Graph frame did not complete within ten seconds.");
        }

        byte[] actual = new byte[ByteCount];
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Frame seed {seed} observed stale GPU data.");
    }

    private static AdapterInfo SelectWarp(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
            confirmed != adapters.Length)
        {
            throw new InvalidOperationException("The adapter set changed during enumeration.");
        }

        AdapterInfo warp = adapters.FirstOrDefault(static adapter => !adapter.HardwareAccelerated);
        return warp.Name.Length != 0
            ? warp
            : throw new NotSupportedException("The Direct3D 12 WARP adapter is unavailable.");
    }

    private static byte[] CreateInput(byte seed)
    {
        var bytes = new byte[ByteCount];
        for (int index = 0; index < bytes.Length; index++)
            bytes[index] = unchecked((byte)(index * 29 + seed));
        return bytes;
    }
}
