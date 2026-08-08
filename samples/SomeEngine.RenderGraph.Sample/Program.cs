using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.RenderGraph.Sample;

internal sealed class CopyPassData
{
    public BufferHandle Source;
    public BufferHandle Destination;
}

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

        using IGraphicsBackend backend = new D3D12Backend();
        AdapterInfo warp = SelectWarp(backend);
        DeviceQueueDesc[] queues =
        [
            new(QueueType.Graphics),
            new(QueueType.Copy),
        ];
        using Device device = backend.CreateDevice(new DeviceDesc(
            warp.Id,
            RetirementType.Automatic,
            queues,
            label: "Render Graph sample"));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination, "Sample readback"),
            MemoryType.Readback);

        ExecuteFrame(backend, device, readback, seed: 17);
        ExecuteFrame(backend, device, readback, seed: 91);
        backend.CollectCompleted(device);
        Console.WriteLine("Executed two independently compiled D3D12 Render Graph frames on WARP.");
        return 0;
    }

    private static void ExecuteFrame(
        IGraphicsBackend backend,
        Device device,
        Buffer readback,
        byte seed)
    {
        byte[] expected = CreateInput(seed);
        using var graph = new RenderGraph(backend, device);
        BufferHandle source = graph.CreateUploadBuffer(expected, "Sample upload");
        BufferHandle transient = graph.CreateBuffer(new BufferDesc(
            ByteCount,
            BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Sample transient"));
        BufferHandle destination = graph.Import(
            readback,
            GraphResourceUsage.CopyDestination,
            GraphResourceUsage.CopyDestination,
            contentsAvailable: false);

        RecordCopy(graph, "Stage upload", source, transient, QueueType.Copy);
        RecordCopy(graph, "Copy to readback", transient, destination, QueueType.Graphics);

        QueueCompletion[] completions = graph.Execute();
        foreach (ref readonly QueueCompletion completion in completions.AsSpan())
        {
            if (backend.WaitCpu(completion, TimeSpan.FromSeconds(10)) != WaitStatus.Completed)
                throw new TimeoutException("The Render Graph frame did not complete within ten seconds.");
        }

        byte[] actual = new byte[ByteCount];
        BufferRange range = new(0, ByteCount);
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Frame seed {seed} observed stale GPU data.");
    }

    private static void RecordCopy(
        RenderGraph graph,
        string name,
        BufferHandle source,
        BufferHandle destination,
        QueueType queue)
    {
        using IUnsafeRenderGraphBuilder builder = graph.AddUnsafePass<CopyPassData>(
            name,
            out CopyPassData passData,
            queue);
        passData.Source = source;
        passData.Destination = destination;
        builder.UseBuffer(source, GraphResourceUsage.CopySource, GraphAccess.Read);
        builder.UseBuffer(destination, GraphResourceUsage.CopyDestination, GraphAccess.WriteAll);
        builder.SetRenderFunc<CopyPassData>(static (data, context) =>
            context.CopyBufferRegion(data.Source, 0, data.Destination, 0, ByteCount));
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
