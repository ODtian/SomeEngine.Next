using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>
/// Asset-owned render scene. Runtime startup consumes this contract through <see cref="AssetLoader"/>
/// just like meshes, materials, and shaders; the executable contains no default-scene geometry or
/// material identities.
/// </summary>
[BinaryContract(BinaryCompatibility.ExactSchema)]
[global::SomeEngine.Assets.Asset(".scene.asset")]
public sealed partial class RenderScene
{
    public string? AssetGuid { get; set; }

    public string? Name { get; set; }

    public SceneCamera? Camera { get; set; }

    public IList<SceneMeshInstance>? MeshInstances { get; set; }

    public IList<SceneDirectionalLight>? DirectionalLights { get; set; }

    public IList<ScenePointLight>? PointLights { get; set; }

    public IList<SceneSpotLight>? SpotLights { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class SceneCamera
{
    public SceneVector3? Position { get; set; }

    public SceneVector3? Target { get; set; }

    public SceneVector3? Up { get; set; }

    public float VerticalFieldOfView { get; set; }

    public float NearPlane { get; set; }

    public float FarPlane { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class SceneMeshInstance
{
    public string? MeshGuid { get; set; }

    public IList<string>? MaterialGuids { get; set; }

    public SceneVector3? Position { get; set; }

    public SceneQuaternion? Rotation { get; set; }

    public SceneVector3? Scale { get; set; }

    public float BoundsExpansion { get; set; }

    public SceneVector3? MotionAmplitude { get; set; }

    public uint MotionSeed { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class SceneDirectionalLight
{
    public SceneVector3? Direction { get; set; }

    public SceneVector3? Color { get; set; }

    public float Intensity { get; set; }

    public uint LayerMask { get; set; } = uint.MaxValue;
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class ScenePointLight
{
    public SceneVector3? Position { get; set; }

    public float Range { get; set; }

    public SceneVector3? Color { get; set; }

    public float Intensity { get; set; }

    public uint LayerMask { get; set; } = uint.MaxValue;
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class SceneSpotLight
{
    public SceneVector3? Position { get; set; }

    public float Range { get; set; }

    public SceneVector3? Direction { get; set; }

    public float InnerConeCos { get; set; }

    public float OuterConeCos { get; set; }

    public SceneVector3? Color { get; set; }

    public float Intensity { get; set; }

    public uint LayerMask { get; set; } = uint.MaxValue;
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class SceneVector3
{
    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class SceneQuaternion
{
    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float W { get; set; } = 1.0f;
}
