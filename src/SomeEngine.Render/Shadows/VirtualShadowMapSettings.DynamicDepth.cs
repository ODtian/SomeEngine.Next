namespace SomeEngine.Render.Shadows;

public sealed partial record VirtualShadowMapSettings
{
    /// <summary>Refreshes directional receiver-mask dynamic depth each frame when enabled.</summary>
    public bool DirectionalDynamicReceiverRefresh { get; init; } = true;

    /// <summary>Applies the dynamic-depth choice to a view's existing flags during view preparation.</summary>
    public uint ApplyDirectionalDynamicReceiverRefresh(uint flags, VirtualShadowLightKind lightKind) =>
        lightKind == VirtualShadowLightKind.Directional && !DirectionalDynamicReceiverRefresh
            ? flags | VirtualShadowViewFlags.DirectionalDynamicDepthCache
            : flags & ~VirtualShadowViewFlags.DirectionalDynamicDepthCache;
}
