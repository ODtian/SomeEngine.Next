using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's equivalent surface modifiers, written with public APIs.</summary>
public static class SurfaceModifiers
{
    public static Entity Background(this Entity entity, Color fill)
    {
        Scope.Write<Surface>(entity).Fill = fill;
        return entity;
    }

    public static Entity Corner(this Entity entity, float radius)
    {
        Scope.Write<Surface>(entity).CornerRadius = radius;
        return entity;
    }

    public static Entity Border(this Entity entity, Color color, float width)
    {
        ref Surface surface = ref Scope.Write<Surface>(entity);
        surface.BorderColor = color;
        surface.BorderWidth = width;
        return entity;
    }
}
