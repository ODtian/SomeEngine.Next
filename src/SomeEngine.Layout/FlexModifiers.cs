using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Layout;

public static class FlexModifiers
{
    public static Entity Gap(this Entity entity, float value)
    {
        DevelopmentCheck.Require(Scope.World.Has<Flex>(entity), "Gap requires a Flex view.");
        Scope.Write<Flex>(entity).Gap = value;
        return entity;
    }

    public static Entity Align(this Entity entity, Align main, Align cross = SomeEngine.Layout.Align.Stretch)
    {
        DevelopmentCheck.Require(Scope.World.Has<Flex>(entity), "Align requires a Flex view.");
        ref Flex flex = ref Scope.Write<Flex>(entity);
        flex.MainAlign = main;
        flex.CrossAlign = cross;
        return entity;
    }

    public static Entity Grow(this Entity entity, float value = 1)
    {
        Scope.Write<FlexItem>(entity).Grow = value;
        return entity;
    }

    public static Entity Shrink(this Entity entity, float value = 1)
    {
        Scope.Write<FlexItem>(entity).Shrink = value;
        return entity;
    }

    public static Entity Basis(this Entity entity, float value)
    {
        Scope.Write<FlexItem>(entity).Basis = value;
        return entity;
    }

    public static Entity AlignSelf(this Entity entity, Align value)
    {
        Scope.Write<FlexItem>(entity).Self = value;
        return entity;
    }
}
