using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>Stores caller-provided environment components on view nodes.</summary>
public static class EnvironmentExtensions
{
    /// <summary>Writes environment data through the active build's component copy and returns the node.</summary>
    public static Entity Provide<T>(this Entity node, in T value) where T : struct
    {
        Scope.Write<T>(node) = value;
        return node;
    }
}
