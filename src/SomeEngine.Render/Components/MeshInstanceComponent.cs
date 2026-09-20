using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Assets.Pipeline;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

public struct MeshInstance : IComponent
{
    public required Mesh Mesh;
    public float BoundsExpansion;
    /// <summary>Optional explicit source for geometry pages not yet resident in Mesh.</summary>
    public MeshPayloadSource? PageSource;
}

