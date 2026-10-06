using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private GraphBufferInput _surfaceReceivers = null!;
    private GraphBufferInput _additionalReceivers = null!;
    private GraphBufferSlice _additionalReceiverData;
    private uint _additionalReceiverCount;

    /// <summary>Supplies user-shader water, translucency or hair receiver layers in the current render view.</summary>
    public void SetAdditionalReceiverInputs(GraphBufferSlice receivers, uint layerCount)
    {
        if (_frameActive) throw new InvalidOperationException("Receiver layers must be set before staging a shadow frame.");
        _additionalReceiverData = receivers;
        _additionalReceiverCount = layerCount;
    }
}
