using System.Numerics;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Shadows;

public enum VirtualShadowDistantLightMode
{
    Disabled,
    Conservative,
    All,
}

public sealed partial record VirtualShadowMapSettings
{
    public VirtualShadowDistantLightMode DistantLightMode { get; init; } = VirtualShadowDistantLightMode.Conservative;
    public int MaximumDistantUpdatesPerFrame { get; init; } = 1;

    /// <summary>Smallest mip requested by a receiver inside the local light's bounding sphere.</summary>
    public uint CalculateConservativeLocalMip(in RenderView receiver, Vector3 lightPosition,
        float lightRange, float shadowProjectionScale, float resolutionLodBias, float globalResolutionLodBias = 0f)
    {
        Vector3 viewPosition = Vector3.Transform(lightPosition, receiver.View);
        float nominalRadius = MathF.Min(
            1f / (receiver.ViewportWidth * MathF.Abs(receiver.Projection.M11)),
            1f / (receiver.ViewportHeight * MathF.Abs(receiver.Projection.M22)));
        float closestDepth = MathF.Max(0f, -viewPosition.Z - lightRange);
        float worldRadius = nominalRadius * (closestDepth * MathF.Abs(receiver.Projection.M34) + MathF.Abs(receiver.Projection.M44));
        float footprint = worldRadius * MathF.Abs(shadowProjectionScale) * VirtualResolution / lightRange;
        int mip = Math.Clamp((int)MathF.Floor(MathF.Log2(MathF.Max(footprint, 1e-20f)) + resolutionLodBias + globalResolutionLodBias),
            0, checked((int)MipLevelCount - 1));
        if (LocalMipMode == VirtualShadowLocalMipMode.Even) mip &= ~1;
        else if (LocalMipMode == VirtualShadowLocalMipMode.Odd) mip = Math.Min((mip & ~1) + 1, checked((int)MipLevelCount - 1));
        return checked((uint)mip);
    }
}
