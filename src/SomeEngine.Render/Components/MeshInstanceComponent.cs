using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

public struct MeshInstance : IComponent
{
    public AssetHandle<Mesh> Mesh;
    public float BoundsExpansion;
}

