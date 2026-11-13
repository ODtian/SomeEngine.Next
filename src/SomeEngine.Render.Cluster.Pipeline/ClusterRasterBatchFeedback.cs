using System.Buffers.Binary;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

internal sealed class ClusterRasterBatchFeedback : IGraphReadbackReceiver
{
    internal uint Required { get; private set; }
    internal ulong FrameIdentity { get; private set; }

    public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
    {
        Required = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        FrameIdentity = tag;
    }
}
