using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    internal void AddTagId(Entity entity, int componentId)
    {
        _components.AddTag(entity, componentId);
    }

    internal void RemoveTagId(Entity entity, int componentId)
    {
        _components.RemoveTag(entity, componentId);
    }
}

