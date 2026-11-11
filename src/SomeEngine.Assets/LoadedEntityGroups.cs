using SomeEngine.ECS.Entities;

namespace SomeEngine.Assets;

public sealed class LoadedEntityGroups
{
    internal readonly Dictionary<int, Entity[]> Values = [];

    public bool TryGetEntities(int contentIndex, out ReadOnlyMemory<Entity> entities)
    {
        if (Values.TryGetValue(contentIndex, out Entity[]? members))
        {
            entities = members;
            return true;
        }
        entities = default;
        return false;
    }
}
