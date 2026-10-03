using static SomeEngine.Layout.AbsoluteFunctions;
using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;

namespace SomeEngine.Views.AotSmoke;

/// <summary>Public layout behavior shared by unit tests and the NativeAOT executable.</summary>
internal static partial class LayoutChecks
{
    internal static void Run()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600), Scale = 2 });
        Entity root = Views.Spawn(world, canvas, new Content());
        systems.Update();
        Entity absolute = Child(world, root);
        Entity custom = Child(world, absolute);
        Entity nested = Child(world, custom);
        Entity leaf = Child(world, nested);
        Require(world.Read<LayoutRect>(absolute).Value == new Rect(0, 0, 400, 300), "Canvas scale defines the available size.");
        Require(world.Read<LayoutRect>(custom).Value == new Rect(10, 20, 100, 80), "Box defines the user layout's outer size.");
        Require(world.Read<LayoutRect>(nested).Value == new Rect(19, 31, 20, 30), "The user layout places nested Absolute content.");
        Require(world.Read<LayoutRect>(leaf).Value == new Rect(21, 34, 8, 9), "Nested placements resolve in canvas coordinates.");

        world.Replace(canvas, new Canvas { Size = new Size(600, 400), Scale = 2 });
        systems.Update();
        Require(world.Read<LayoutRect>(absolute).Value == new Rect(0, 0, 300, 200), "Changing canvas size updates its layout roots.");
    }

    private static Entity Child(World world, Entity parent)
    {
        var children = Hierarchy<ElementTree>.GetChildren(world, parent);
        Require(children.Count == 1, "This layout example declares one child at each level.");
        return children[0];
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct Content : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (Scope.View(new OffsetLayout(), 2).Position(10, 20).Size(100, 80).Padding(5).Content())
            using (Absolute().Size(20, 30).Content())
                Absolute().Size(8, 9).Position(2, 3);
            return default;
        }
    }

    private readonly record struct OffsetLayout : ILayout, ISparseComponent
    {
        public static ChildSource Children => ChildSource.Caller;
        public BuildTask Build() => default;

        Size IView.Measure(ref LayoutPass pass, in Constraints constraints)
        {
            var available = new Constraints(default, constraints.Max);
            foreach (Entity child in pass.Children)
            {
                pass.Measure(child, in available);
                pass.Place(child, new Vector2(4, 6));
            }
            return constraints.Max;
        }
    }
}
