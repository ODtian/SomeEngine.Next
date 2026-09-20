namespace SomeEngine.Assets.Schema;

[Asset(".runtime.asset", SchemaName = "SomeEngine.Assets.Schema.RuntimeConfiguration")]
public sealed partial class ApplicationSettings
{
    public string Name { get; set; } = "SomeEngine";
    [AssetReference(typeof(SomeEngine.ECS.World))]
    public Guid Scene { get; set; }
    [AssetReference(typeof(ClusterShaders))]
    public Guid Pipeline { get; set; }
    [AssetReference(typeof(VirtualShadowMapShaders))]
    public Guid VirtualShadowMap { get; set; }
    [AssetReference(typeof(Shader))]
    public Guid UiShader { get; set; }
    public string UiVertexEntry { get; set; } = "vertexMain";
    public string UiPixelEntry { get; set; } = "fragmentMain";
    [AssetReference(typeof(Shader))]
    public Guid PresentationShader { get; set; }
    public string PresentationVertexEntry { get; set; } = "vertexMain";
    public string PresentationPixelEntry { get; set; } = "fragmentMain";
    [AssetReference(typeof(Shader))]
    public Guid LightingShader { get; set; }
    public string LightingEntryPoint { get; set; } = "main";
    [AssetReference(typeof(Shader))]
    public Guid TemporalShader { get; set; }
    public string TemporalVertexEntry { get; set; } = "vertexMain";
    public string TemporalPixelEntry { get; set; } = "fragmentMain";
    [AssetReference(typeof(Shader))]
    public Guid TranslucencyShader { get; set; }
    public string TranslucencyVertexEntry { get; set; } = "vertexMain";
    public string TranslucencyPixelEntry { get; set; } = "fragmentMain";
    [AssetReference(typeof(Shader))]
    public Guid DebugShader { get; set; }
    public string DebugVertexEntry { get; set; } = "vertexMain";
    public string DebugPixelEntry { get; set; } = "fragmentMain";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public string Title => Name;

    public static ApplicationSettings Create(ApplicationSettings.Asset asset)
        => CreateValueGenerated(asset.CopyValues());
}
