namespace SomeEngine.Assets.Schema;

public sealed record ApplicationSettings(
        Guid Scene,
        Guid Pipeline,
        Guid VirtualShadowMap,
        Guid UiShader,
        string UiVertexEntry,
        string UiPixelEntry,
        Guid PresentationShader,
        string PresentationVertexEntry,
        string PresentationPixelEntry,
        Guid LightingShader,
        string LightingEntryPoint,
        Guid TemporalShader,
        string TemporalVertexEntry,
        string TemporalPixelEntry,
        Guid TranslucencyShader,
        string TranslucencyVertexEntry,
        string TranslucencyPixelEntry,
        Guid DebugShader,
        string DebugVertexEntry,
        string DebugPixelEntry,
        int Width,
        int Height,
        string Title)

{
    public static ApplicationSettings Create(Asset<ApplicationSettings> asset)
    {
        ApplicationData value = asset.GetFieldsGenerated<ApplicationData>();
        (Guid id, string vertex, string pixel) Raster(IList<ShaderRef> refs)
        {
            ShaderRef vertex = refs.Single(r => r.Stage == ShaderStage.Vertex);
            ShaderRef pixel = refs.Single(r => r.Stage == ShaderStage.Pixel);
            return (Guid.Parse(vertex.AssetGuid!), vertex.EntryPoint!, pixel.EntryPoint!);
        }
        var ui = Raster(value.UiShaders!);
        var presentation = Raster(value.PresentationShaders!);
        var temporal = Raster(value.TemporalShaders!);
        var translucency = Raster(value.TranslucencyShaders!);
        var debug = Raster(value.DebugShaders!);
        return new(Guid.Parse(value.SceneGuid!), Guid.Parse(value.ClusterPipelineGuid!),
            Guid.Parse(value.VirtualShadowMapGuid!), ui.id, ui.vertex, ui.pixel,
            presentation.id, presentation.vertex, presentation.pixel,
            Guid.Parse(value.LightingShader!.AssetGuid!), value.LightingShader.EntryPoint!,
            temporal.id, temporal.vertex, temporal.pixel, translucency.id, translucency.vertex, translucency.pixel,
            debug.id, debug.vertex, debug.pixel, checked((int)value.WindowWidth), checked((int)value.WindowHeight),
            value.Name ?? "SomeEngine");
    }
}
