using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class CrossQueueCopyTests
{
    [Fact]
    public void PersistentGraphExecutesTwoCrossQueueFrames()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer upload = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = BuildCopyGraph(support, upload, readback);

        foreach (byte seed in new byte[] { 17, 91 })
        {
            byte[] expected = Enumerable.Range(0, ByteCount)
                .Select(index => unchecked((byte)(index * 31 + seed))).ToArray();
            BufferRange range = new(0, ByteCount);
            using (MappedBuffer mapping = support.Backend.Map(upload, MapType.Write, range))
            {
                expected.CopyTo(mapping.Bytes);
                mapping.Flush(range);
            }

            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            using (RenderGraphFrame frame = graph.BeginFrame())
                _ = frame.Execute(completions);
            support.Wait(completions);

            using MappedBuffer result = support.Backend.Map(readback, MapType.Read, range);
            result.Invalidate(range);
            Assert.Equal(expected, result.Bytes.ToArray());
        }
        Assert.Equal(1UL, graph.StructureVersion);
    }
}
