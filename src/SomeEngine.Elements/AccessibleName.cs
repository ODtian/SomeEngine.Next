using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>The displayed text or business label identifying a node.</summary>
public record struct AccessibleName : IComponent
{
    public string Value;
}
