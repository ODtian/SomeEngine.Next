using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>The purpose of a node's content or control.</summary>
public enum Role : byte
{
    Group,
    Text,
    Image,
    Button,
    Toggle,
    Slider,
    TextField,
    Dialog,
    Heading,
    List,
}

/// <summary>The accessibility role written by a view or its caller.</summary>
public record struct AccessibleRole : IComponent
{
    public Role Value;
}
