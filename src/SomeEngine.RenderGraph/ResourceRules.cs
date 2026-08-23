namespace SomeEngine.RenderGraph;

internal static class ResourceAccessRules
{
    private const ResourceAccess WriteMask =
        ResourceAccess.RenderTarget |
        ResourceAccess.UnorderedAccess |
        ResourceAccess.DepthStencilWrite |
        ResourceAccess.StreamOutput |
        ResourceAccess.CopyDestination |
        ResourceAccess.ResolveDestination |
        ResourceAccess.RayTracingAccelerationStructureWrite;

    internal static bool Writes(ResourceAccess access) => (access & WriteMask) != 0;
}

internal static class TextureFormatRules
{
    internal static TextureAspects Aspects(Format format) => format switch
    {
        Format.D16UNorm or Format.D32Float => TextureAspects.Depth,
        Format.D24UNormS8UInt or Format.D32FloatS8UInt =>
            TextureAspects.Depth | TextureAspects.Stencil,
        _ => TextureAspects.Color,
    };
}
