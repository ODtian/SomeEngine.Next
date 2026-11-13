using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>Expands each changed current/previous caster bound over its allocated shadow pages.</summary>
public static class VirtualShadowCasterExpansionPasses
{
    public static ScreenBinningExpansionResources Add(ref RenderGraphFrame frame, RenderPipelines pipelines,
        Shader shader, IndirectCommandLayout dispatchLayout, uint changedInstances, uint viewCount, uint mipCount,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolveResource, GraphRegionId region)
    {
        uint items = checked(changedInstances * viewCount * mipCount * 2u);
        return ScreenBinningExpansionPasses.Add(ref frame, pipelines, shader, shader, dispatchLayout,
            items, resolveResource, region);
    }
}
