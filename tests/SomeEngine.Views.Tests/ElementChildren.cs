using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

public static class ElementChildren
{
    public static Entity[] Read(World world, Entity parent)
    {
        using ChildrenRead children = Hierarchy<ElementTree>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }
}
