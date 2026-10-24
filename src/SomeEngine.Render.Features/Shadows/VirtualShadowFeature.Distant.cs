using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    /// <summary>Completed asynchronous page-pressure LOD bias; the tag is the originating shadow frame.</summary>
    public event Action<ulong, float>? ResolutionBiasCompleted;

    private GraphReadbackId _distantBiasReadback;
    private readonly Dictionary<ulong, DistantBiasFrame> _distantBiasFrames = [];
    private sealed class DistantBiasFrame
    {
        internal bool Complete;
        internal float? LocalBias;
    }

    private void InstallDistantBias(ref RenderGraphEdit edit, GraphRegionId feedback)
    {
        if (!_shadowMap.Settings.DynamicResolutionEnabled) return;
        _distantBiasReadback = edit.AddReadback(_dynamicResolutionBias, BufferRange.Whole, new DistantBiasReceiver(this),
            "Observe local shadow page pressure", feedback);
        edit.OrderAfter(_distantBiasReadback, _dynamicResolutionPass);
    }

    private void StageDistantBias(ref RenderGraphFrame graph)
    {
        if (!_shadowMap.Settings.DynamicResolutionEnabled) return;
        _distantBiasFrames.Add(_frameIndex, new DistantBiasFrame());
        graph.SetReadbackTag(_distantBiasReadback, _frameIndex);
        graph.SetReadbackEnabled(_distantBiasReadback, true);
    }

    private void CommitDistantBias()
    {
        if (!_distantBiasFrames.TryGetValue(_frameIndex, out DistantBiasFrame? frame)) return;
        frame.Complete = true;
        AcceptDistantBias(_frameIndex, frame);
    }

    private void AcceptDistantBias(ulong tag, DistantBiasFrame frame)
    {
        if (!frame.Complete || frame.LocalBias is not { } bias) return;
        _shadowMap.UpdateGlobalLocalResolutionLodBias(bias);
        _distantBiasFrames.Remove(tag);
        ResolutionBiasCompleted?.Invoke(tag, bias);
    }

    private sealed class DistantBiasReceiver(VirtualShadowFeature feature) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            if (!feature._distantBiasFrames.TryGetValue(tag, out DistantBiasFrame? frame)) return;
            frame.LocalBias = MemoryMarshal.Cast<byte, float>(bytes)[0];
            feature.AcceptDistantBias(tag, frame);
        }
    }
}
