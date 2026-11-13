using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>Writes the first regular shadow projection for each GPU light index.</summary>
public static class VirtualShadowLightViews
{
    public static void WriteIndices(scoped ReadOnlySpan<VirtualShadowView> views, scoped Span<uint> firstViewIndices)
    {
        firstViewIndices.Fill(uint.MaxValue);
        for (int index = 0; index < views.Length; index++)
        {
            ref readonly VirtualShadowView view = ref views[index];
            if ((view.Flags & VirtualShadowViewFlags.FirstPersonShadow) != 0u) continue;
            if (view.LightKind == VirtualShadowLightKind.Directional
                ? view.ClipmapLevel != view.FirstClipmapLevel
                : view.FaceIndex != 0u) continue;
            firstViewIndices[(int)view.LightIndex] = (uint)index;
        }
    }
}
