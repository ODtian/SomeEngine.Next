using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

public static class ContentExtensions
{
    /// <summary>Opens a container's complete child declaration within the active build.</summary>
    public static ParentScope Content(this Entity node) => Scope.EnterContent(node);
}
