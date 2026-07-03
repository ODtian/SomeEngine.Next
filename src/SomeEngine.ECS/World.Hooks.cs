using SomeEngine.ECS.Components;
using SomeEngine.ECS.Hooks;

namespace SomeEngine.ECS;

public partial class World
{
    internal bool HasHooks => _hooks.Any;

    public ComponentHooks<T> Hooks<T>()
        where T : struct, IComponent
    {
        return _hooks.View<T>();
    }
}

