using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.ApplicationSettingsBlock")]
[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Scene), nameof(Pipeline), nameof(VirtualShadowMap), nameof(UiShader), nameof(UiVertexEntry), nameof(UiPixelEntry), nameof(PresentationShader), nameof(PresentationVertexEntry), nameof(PresentationPixelEntry), nameof(LightingShader), nameof(LightingEntryPoint), nameof(TemporalShader), nameof(TemporalVertexEntry), nameof(TemporalPixelEntry), nameof(TranslucencyShader), nameof(TranslucencyVertexEntry), nameof(TranslucencyPixelEntry), nameof(DebugShader), nameof(DebugVertexEntry), nameof(DebugPixelEntry), nameof(Width), nameof(Height))]
public sealed partial class ApplicationSettingsBlock : AssetBlock
{
    public partial AssetId<WorldAsset> Scene { get; set; }
    public partial AssetId<ClusterShadersAsset> Pipeline { get; set; }
    public partial AssetId<VirtualShadowMapShadersAsset> VirtualShadowMap { get; set; }
    public partial AssetId<ShaderAsset> UiShader { get; set; }
    public partial string UiVertexEntry { get; set; }
    public partial string UiPixelEntry { get; set; }
    public partial AssetId<ShaderAsset> PresentationShader { get; set; }
    public partial string PresentationVertexEntry { get; set; }
    public partial string PresentationPixelEntry { get; set; }
    public partial AssetId<ShaderAsset> LightingShader { get; set; }
    public partial string LightingEntryPoint { get; set; }
    public partial AssetId<ShaderAsset> TemporalShader { get; set; }
    public partial string TemporalVertexEntry { get; set; }
    public partial string TemporalPixelEntry { get; set; }
    public partial AssetId<ShaderAsset> TranslucencyShader { get; set; }
    public partial string TranslucencyVertexEntry { get; set; }
    public partial string TranslucencyPixelEntry { get; set; }
    public partial AssetId<ShaderAsset> DebugShader { get; set; }
    public partial string DebugVertexEntry { get; set; }
    public partial string DebugPixelEntry { get; set; }
    public partial int Width { get; set; }
    public partial int Height { get; set; }

    partial void Initialize()
    {
        UiVertexEntry = "vertexMain";
        UiPixelEntry = "fragmentMain";
        PresentationVertexEntry = "vertexMain";
        PresentationPixelEntry = "fragmentMain";
        LightingEntryPoint = "main";
        TemporalVertexEntry = "vertexMain";
        TemporalPixelEntry = "fragmentMain";
        TranslucencyVertexEntry = "vertexMain";
        TranslucencyPixelEntry = "fragmentMain";
        DebugVertexEntry = "vertexMain";
        DebugPixelEntry = "fragmentMain";
        Width = 1280;
        Height = 720;
    }
}
