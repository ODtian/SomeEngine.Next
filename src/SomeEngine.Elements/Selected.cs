using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>Selection toggled by each completed pointer click.</summary>
public struct Selected : IComponent
{
    public bool Value;
}
