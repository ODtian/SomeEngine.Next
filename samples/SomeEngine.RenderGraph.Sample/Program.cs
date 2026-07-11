using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using SomeEngine.RenderGraph;

namespace SomeEngine.RenderGraph.Sample;

internal static class Program
{
    private const int ByteCount = 256;

    public static int Main()
    {
        using Device device = new();
        using RenderGraph graph = new(device);

        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(ByteCount, BufferUsage.CopySource, "Sample upload"),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(ByteCount, BufferUsage.CopyDestination, "Sample readback"),
            MemoryType.Readback);

        try
        {
            ExecuteFrame(device, graph, upload, readback, seed: 17);
            ExecuteFrame(device, graph, upload, readback, seed: 91);

            RenderGraphStatistics graphStatistics = graph.Statistics;
            Statistics deviceStatistics = device.Statistics;
            if (graphStatistics.CacheMisses != 1 ||
                graphStatistics.CacheHits != 1 ||
                graphStatistics.ConservativeCompilations != 1)
            {
                throw new InvalidOperationException(
                    "Two structurally equal immediate recordings did not reuse one transparent compiled plan.");
            }
            Console.WriteLine(
                $"Executed two immediate frames through one transparent compiled-plan cache entry: " +
                $"misses={graphStatistics.CacheMisses}, " +
                $"hits={graphStatistics.CacheHits}, " +
                $"passes={graphStatistics.CommandListsRecorded}, " +
                $"submissions={graphStatistics.Submissions}, " +
                $"executedCopies={deviceStatistics.ExecutedCopies}.");
            return 0;
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
            device.CollectGarbage();
        }
    }

    private static void ExecuteFrame(
        Device device,
        RenderGraph graph,
        BufferHandle upload,
        BufferHandle readback,
        byte seed)
    {
        byte[] expected = CreateInput(seed);
        device.WriteBuffer(upload, 0, expected);

        GraphBuilder builder = graph.Begin();
        BufferId source = builder.ImportBuffer(
            upload,
            BufferUse.CopySource,
            BufferUse.CopySource);
        BufferId transient = builder.CreateBuffer(
            new BufferDesc(ByteCount, BufferUsage.CopySource | BufferUsage.CopyDestination, "Sample transient"));
        BufferId destination = builder.ImportBuffer(
            readback,
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            contentsAvailable: false);

        PassBuilder stageUpload = builder.AddPass("Stage upload", new QueueSelection(QueueType.Copy));
        BufferAccess uploadRead = stageUpload.Read(source, BufferUse.CopySource);
        BufferAccess transientWrite = stageUpload.Write(transient, BufferUse.CopyDestination);
        stageUpload.Execute((ICommandContext commands, in PassResources resources) =>
            commands.CopyBuffer(resources.Get(uploadRead), 0, resources.Get(transientWrite), 0, ByteCount));

        PassBuilder copyToReadback = builder.AddPass("Copy to readback", new QueueSelection(QueueType.Copy));
        BufferAccess transientRead = copyToReadback.Read(transient, BufferUse.CopySource);
        BufferAccess readbackWrite = copyToReadback.Write(destination, BufferUse.CopyDestination);
        copyToReadback.Execute((ICommandContext commands, in PassResources resources) =>
            commands.CopyBuffer(resources.Get(transientRead), 0, resources.Get(readbackWrite), 0, ByteCount));

        GraphExecution execution = graph.Execute(ref builder);
        if (!execution.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The Null render-graph execution did not complete within five seconds.");
        }

        byte[] actual = new byte[ByteCount];
        device.ReadBuffer(readback, 0, actual);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException($"Frame seed {seed} observed stale invocation payload.");
        }
    }

    private static byte[] CreateInput(byte seed)
    {
        byte[] bytes = new byte[ByteCount];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = unchecked((byte)(index * 29 + seed));
        }

        return bytes;
    }
}
