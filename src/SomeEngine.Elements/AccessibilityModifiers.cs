using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>Writes a node's accessibility data through the public computation scope.</summary>
public static class AccessibilityModifiers
{
    public static Entity Accessible(this Entity node, Role role, string name)
    {
        DevelopmentCheck.Require(role is >= Role.Group and <= Role.List,
            "Accessible requires a defined Role.");
        DevelopmentCheck.Require(name is not null, "Accessible requires a name string.");
        Scope.Write<AccessibleRole>(node).Value = role;
        Scope.Write<AccessibleName>(node).Value = name;
        return node;
    }
}
