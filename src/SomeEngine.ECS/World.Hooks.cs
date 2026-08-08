using SomeEngine.ECS.Components;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    protected internal bool HasValueReplaceHookCallbacks(int componentId) =>
        _hooks.HasValueReplaceCallbacks(componentId);

    internal bool HasCreateHookCallbacks(int componentId) =>
        _hooks.HasCreateCallbacks(componentId);

    public ComponentHooks<T> Hooks<T>()
        where T : struct, IComponent
    {
        if (ComponentMetadata<T>.IsRelationshipSource ||
            ComponentMetadata<T>.IsRelationshipTarget)
        {
            throw new InvalidOperationException(
                $"Component hooks for relationship component {typeof(T).Name} are ECS-internal. " +
                "Observe its native Added/Changed/Removed facts from a system instead.");
        }

        return _hooks.View<T>();
    }
}

