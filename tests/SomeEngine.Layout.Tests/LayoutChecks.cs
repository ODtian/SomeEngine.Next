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
        VerifyRepeatedMeasurements();
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

        uint canvasVersion = world.WriteVersion<LayoutRect>(canvas);
        uint absoluteVersion = world.WriteVersion<LayoutRect>(absolute);
        uint customVersion = world.WriteVersion<LayoutRect>(custom);
        uint nestedVersion = world.WriteVersion<LayoutRect>(nested);
        world.Replace(leaf, new Box { Fixed = new Size(6, 7) });
        systems.Update();
        Require(world.Read<LayoutRect>(leaf).Value == new Rect(21, 34, 6, 7), "A local size change updates the leaf inside fixed-size layouts.");
        Require(world.WriteVersion<LayoutRect>(canvas) == canvasVersion &&
            world.WriteVersion<LayoutRect>(absolute) == absoluteVersion &&
            world.WriteVersion<LayoutRect>(custom) == customVersion &&
            world.WriteVersion<LayoutRect>(nested) == nestedVersion,
            "The surrounding rectangle versions remain equal after the local size change.");

        foreach (Entity entity in new[] { absolute, custom, nested, leaf })
            world.Remove<LayoutCache>(entity);
        systems.Update();
        Require(world.Read<LayoutRect>(leaf).Value == new Rect(21, 34, 6, 7), "Full measurement after clearing caches preserves the leaf rectangle.");

        world.Replace(canvas, new Canvas { Size = new Size(600, 400), Scale = 2 });
        systems.Update();
        Require(world.Read<LayoutRect>(absolute).Value == new Rect(0, 0, 300, 200), "Changing canvas size updates its layout roots.");
    }

    internal static void VerifyRepeatedMeasurements()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Spawn(world, canvas, new RepeatedMeasurements());
        systems.Update();
        var children = Hierarchy<ElementTree>.GetChildren(world, root);
        Entity container = children[0];
        Entity sibling = children[1];
        Entity leaf = Child(world, container);
        Entity[] nodes = [canvas, root, container, sibling, leaf];
        Require(world.Read<LayoutRect>(sibling).Value == new Rect(100, 0, 10, 10),
            "The custom layout places the sibling after the container's measured width.");

        foreach (float width in new[] { 60f, 140f, 45f })
        {
            world.Replace(leaf, new MeasuredSize(new Size(width / 2, 30)));
            systems.Update();
            world.Replace(container, new Box { Fixed = new Size(width, 80) });
            systems.Update();
            Require(world.Read<LayoutRect>(sibling).Value == new Rect(width, 0, 10, 10),
                "Consecutive content and container changes update the sibling's position.");
            Rect[] incremental = nodes.Select(node => world.Read<LayoutRect>(node).Value).ToArray();
            foreach (Entity node in nodes)
                if (world.Has<LayoutCache>(node))
                    world.Remove<LayoutCache>(node);
            systems.Update();
            Require(incremental.SequenceEqual(nodes.Select(node => world.Read<LayoutRect>(node).Value)),
                "Incremental rectangles match a full measurement after clearing public caches.");
        }
    }

    private readonly record struct RepeatedMeasurements : ILayout, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Size(100, 80).Content())
                Scope.View(new MeasuredSize(new Size(20, 10)), 2);
            Scope.View(new MeasuredSize(new Size(10, 10)), 3);
            return default;
        }

        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            var available = new Constraints(default, constraints.Max);
            float x = 0;
            foreach (Entity child in pass.Children)
            {
                Size size = pass.Measure(child, in available);
                var allocation = Constraints.Fixed(size);
                pass.Measure(child, in allocation);
                pass.Place(child, new Vector2(x, 0));
                x += size.Width;
            }
            return constraints.Max;
        }
    }

    private readonly record struct MeasuredSize(Size Value) : ILayout, IComponent
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints) => Value;
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
