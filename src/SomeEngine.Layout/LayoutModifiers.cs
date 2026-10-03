using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Layout;

public static class LayoutModifiers
{
    public static Entity Padding(this Entity entity, float all)
    {
        Scope.Write<Box>(entity).Padding = new Thickness(all, all, all, all);
        return entity;
    }

    public static Entity Size(this Entity entity, float width, float height)
    {
        Scope.Write<Box>(entity).Fixed = new Size(width, height);
        return entity;
    }

    public static Entity Position(this Entity entity, float x, float y)
    {
        Scope.Write<AbsolutePosition>(entity).Value = new Vector2(x, y);
        return entity;
    }

    public static Entity MinSize(this Entity entity, float width, float height)
    {
        Scope.Write<Box>(entity).Min = new Size(width, height);
        return entity;
    }

    public static Entity MaxSize(this Entity entity, float width, float height)
    {
        Scope.Write<Box>(entity).Max = new Size(width, height);
        return entity;
    }
}
