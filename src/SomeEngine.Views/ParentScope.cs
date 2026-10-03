using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>Completes a container's children and restores the surrounding declaration when disposed.</summary>
public readonly ref struct ParentScope
{
    private readonly Scope.BuildScope _scope;

    internal ParentScope(Entity entity, Scope.BuildScope scope)
    {
        Entity = entity;
        _scope = scope;
    }

    /// <summary>The container receiving the child views declared within this scope.</summary>
    public Entity Entity { get; }

    public void Dispose()
    {
        try
        {
            _scope.Complete();
        }
        finally
        {
            _scope.Dispose();
        }
    }
}
