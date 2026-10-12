using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Lighting;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Tests;

internal sealed partial class FrameStageScene
{
    internal SurfaceReceiverReadback ObserveSurfaceReceiver(int x, int y)
    {
        GraphRegionId region = Assert.IsType<GraphRegionId>(Outputs.SurfaceReceiverConsumers);
        var receiver = new SurfaceReceiverReadback();
        using RenderGraphEdit edit = _graph.BeginEdit();
        edit.AddReadback(Outputs.SurfaceReceivers,
            new BufferRange(checked((ulong)(y * Width + x) * SurfaceReceiver.SizeInBytes), SurfaceReceiver.SizeInBytes),
            receiver, "Read exported material receiver", region: region);
        edit.Commit();
        return receiver;
    }
}

internal sealed class SurfaceReceiverReadback : IGraphReadbackReceiver
{
    private byte[] _bytes = [];

    internal SurfaceReceiver Value
    {
        get
        {
            Assert.Equal(SurfaceReceiver.SizeInBytes, _bytes.Length);
            return MemoryMarshal.Read<SurfaceReceiver>(_bytes);
        }
    }

    public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => _bytes = bytes.ToArray();
}
