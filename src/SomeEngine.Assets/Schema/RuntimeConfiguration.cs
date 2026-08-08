using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>
/// Selects the ordinary runtime boot scene and renderer.  The executable discovers this asset by
/// type from the manifest; it does not embed scene, mesh, material, shader, or pipeline identities.
/// </summary>
[BinaryContract(BinaryCompatibility.ExactSchema)]
[global::SomeEngine.Assets.Asset(".runtime.asset")]
public sealed partial class RuntimeConfiguration
{
    public string? AssetGuid { get; set; }

    public string? Name { get; set; }

    public string? SceneGuid { get; set; }

    public string? ClusterRendererGuid { get; set; }

    public string? UiShaderGuid { get; set; }

    public uint WindowWidth { get; set; } = 1280;

    public uint WindowHeight { get; set; } = 720;
}
