using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

internal static class EnvironmentLookup
{
    internal static Entity FindSource(World world, Entity entity, int component)
    {
        for (Entity ancestor = entity; ancestor != Entity.Null;
            ancestor = Hierarchy<ElementTree>.GetParent(world, ancestor))
        {
            if (world.Has(ancestor, component))
                return ancestor;
        }
        return Entity.Null;
    }

    internal static bool HasChanges(World world, Entity entity, in Dependency dependency)
    {
        Entity source = FindSource(world, entity, dependency.Component);
        return source != dependency.Entity ||
            (source != Entity.Null &&
                world.ComponentWriteVersion(source, dependency.Component) != dependency.Version);
    }
}
