using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>
/// Selects the ordinary runtime boot scene and renderer.  The executable discovers this asset by
/// type from the manifest; it does not embed scene, mesh, material, shader, or pipeline identities.
/// </summary>
[BinaryContract(BinaryCompatibility.ExactSchema, LogicalName = "SomeEngine.Assets.Schema.RuntimeConfiguration")]
[global::SomeEngine.Assets.Asset(typeof(ApplicationSettings), ".runtime.asset")]
internal sealed partial class ApplicationData
{
    public string? AssetGuid { get; set; }

    public string? Name { get; set; }

    public string? SceneGuid { get; set; }

    public string? ClusterPipelineGuid { get; set; }

    public string? VirtualShadowMapGuid { get; set; }

    public IList<ShaderRef>? UiShaders { get; set; }

    public IList<ShaderRef>? PresentationShaders { get; set; }

    public ShaderRef? LightingShader { get; set; }

    public IList<ShaderRef>? TemporalShaders { get; set; }

    public IList<ShaderRef>? TranslucencyShaders { get; set; }

    public IList<ShaderRef>? DebugShaders { get; set; }

    public uint WindowWidth { get; set; } = 1280;

    public uint WindowHeight { get; set; } = 720;
}
