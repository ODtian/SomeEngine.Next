using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

public static class Views
{
    /// <summary>Creates a root with its parameters and requests its first build for the rebuild phase.</summary>
    /// <param name="parent">The ElementTree parent, or Entity.Null for an independent root.</param>
    public static Entity Spawn<T>(World world, Entity parent, in T view)
        where T : struct, IView, IEquatable<T>
    {
        Entity entity = Create(world, in view);
        if (parent != Entity.Null)
        {
            Hierarchy<ElementTree>.SetChildOrderPolicy(world, parent, ChildOrderPolicy.Ordered);
            Hierarchy<ElementTree>.SetParent(world, entity, parent);
        }
        world.AddTag<BuildRequest>(entity);
        return entity;
    }

    internal static Entity Create<T>(World world, in T view)
        where T : struct, IView, IEquatable<T>
    {
        ushort typeId = ViewRegistration<T>.Id;
        Entity entity = world.CreateEntity(new ViewType { Id = typeId });
        world.AddComponentData(entity, in view);
        if (T.Mode == BuildMode.Rerun)
            world.Add(entity, new ParameterVersion());
        Hierarchy<ElementTree>.SetChildOrderPolicy(world, entity, ChildOrderPolicy.Ordered);
        return entity;
    }
}

internal readonly struct BuildRequest : ITag;

internal readonly record struct ParameterVersion(uint Value) : IComponent;
