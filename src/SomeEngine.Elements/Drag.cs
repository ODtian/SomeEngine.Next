using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>Primary-button drag data written by the pointer input stage.</summary>
public struct Drag : IComponent
{
    public bool Active;
    public Vector2 Delta;
    public Vector2 Total;
    public uint Count;
}
