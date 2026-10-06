using SomeEngine.Render.Components;

namespace SomeEngine.Render.Shadows;

public sealed partial class VirtualShadowMap
{
    private void ConfigureLocalRepresentation(ref VirtualShadowView view, in RenderView receiver, float projectionScale)
    {
        uint minimumMip = Settings.CalculateConservativeLocalMip(receiver, view.LightPosition, view.LightRange,
            projectionScale, view.ResolutionLodBias);
        view.MinMipLevel = minimumMip;
        bool distant = Settings.DistantLightMode == VirtualShadowDistantLightMode.All ||
            (Settings.DistantLightMode == VirtualShadowDistantLightMode.Conservative && minimumMip == Settings.MipLevelCount - 1u);
        if (distant)
        {
            view.MinMipLevel = Settings.MipLevelCount - 1u;
            view.Flags |= VirtualShadowViewFlags.SinglePage | VirtualShadowViewFlags.DistantLight;
        }
    }
}
