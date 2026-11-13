using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Lighting;

internal enum LightingResource
{
    Views,
    Lights,
    Grid,
    Indices,
    CookieAtlas,
}

internal readonly record struct LightingResourceBinding(string Name, ResourceBindingType Type,
    LightingResource Resource, PipelineSync Sync, GraphAccessMode Mode);

internal static class LightingResources
{
    internal const string LightBufferName = "LightBuffer";
    internal const string LightBinningKindsName = "LightBinningKinds";
    internal const string LightBinningViewsName = "LightBinningViews";
    private static readonly LightingResourceBinding[] Bindings = CreateBindings();

    private static LightingResourceBinding[] CreateBindings()
    {
        LightingResourceBinding[] resources =
        [
            new("LightingViews", ResourceBindingType.BufferSrv, LightingResource.Views, PipelineSync.ComputeShading, GraphAccessMode.Read),
            new(LightBufferName, ResourceBindingType.BufferSrv, LightingResource.Lights, PipelineSync.ComputeShading, GraphAccessMode.Read),
            new("ClusteredLightGrid", ResourceBindingType.BufferSrv, LightingResource.Grid, PipelineSync.ComputeShading, GraphAccessMode.Read),
            new("LightIndexList", ResourceBindingType.BufferSrv, LightingResource.Indices, PipelineSync.ComputeShading, GraphAccessMode.Read),
            new("LightingCookieAtlas", ResourceBindingType.TextureSrv, LightingResource.CookieAtlas, PipelineSync.ComputeShading, GraphAccessMode.Read),
        ];
        return resources;
    }

    internal static LightingResourceBinding? Resolve(string name, ResourceBindingType type)
    {
        foreach (LightingResourceBinding binding in Bindings)
            if (binding.Name == name && binding.Type == type) return binding;
        return null;
    }
}
