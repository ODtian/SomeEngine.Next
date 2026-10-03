using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using UserSurface = SomeEngine.Elements.Consumer.Tests.Rewrites.SurfaceModifiers;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Public surface behavior shared with the NativeAOT executable.</summary>
internal static partial class SurfaceChecks
{
    internal static void Run(bool rewritten)
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(world, canvas,
            new SurfaceExample(120, new Color(0.2f, 0.4f, 0.6f, 0.8f), rewritten));
        systems.Update();
        Entity container = Child(world, root);
        Entity panel = Child(world, container);
        Surface surface = world.Read<Surface>(panel);
        Require(surface.Fill == new Color(0.2f, 0.4f, 0.6f, 0.8f), "Background supplies the linear fill color.");
        Require(surface.CornerRadius == 4, "Corner supplies the radius.");
        Require(surface.BorderColor == new Color(0.9f, 0.7f, 0.5f) && surface.BorderWidth == 2,
            "Border supplies its color and width.");
        Require(world.Read<LayoutRect>(panel).Value == new Rect(0, 0, 120, 80),
            "The decorated node keeps its layout size.");
        uint nodeVersion = world.WriteVersion<Surface>(panel);
        uint typeVersion = world.ComponentWriteVersion<Surface>();

        world.Replace(root, new SurfaceExample(200, new Color(0.2f, 0.4f, 0.6f, 0.8f), rewritten));
        systems.Update();
        Require(Child(world, container) == panel, "Rebuilding preserves the decorated node.");
        Require(world.Read<LayoutRect>(panel).Value == new Rect(0, 0, 200, 80),
            "The next build updates the layout size.");
        Require(world.Read<Surface>(panel).Equals(surface), "The next build preserves the surface values.");
        Require(world.WriteVersion<Surface>(panel) == nodeVersion && world.ComponentWriteVersion<Surface>() == typeVersion,
            "Equal surface values preserve their write versions.");

        world.Replace(root, new SurfaceExample(200, new Color(0.1f, 0.3f, 0.5f, 0.7f), rewritten));
        systems.Update();
        Require(world.Read<Surface>(panel).Fill == new Color(0.1f, 0.3f, 0.5f, 0.7f),
            "Changed fill values reach the decorated node.");
        Require(world.WriteVersion<Surface>(panel) != nodeVersion && world.ComponentWriteVersion<Surface>() != typeVersion,
            "Changed surface values advance their write versions.");
    }

    private static Entity Child(World world, Entity parent)
    {
        var children = Hierarchy<ElementTree>.GetChildren(world, parent);
        Require(children.Count == 1, "The example has one child at each level.");
        return children[0];
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct SurfaceExample(float Width, Color Fill, bool Rewritten) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            {
                Entity panel = Absolute().Size(Width, 80);
                if (Rewritten)
                {
                    UserSurface.Background(panel, Fill);
                    UserSurface.Corner(panel, 4);
                    UserSurface.Border(panel, new Color(0.9f, 0.7f, 0.5f), 2);
                }
                else
                {
                    panel.Background(Fill).Corner(4).Border(new Color(0.9f, 0.7f, 0.5f), 2);
                }
            }
            return default;
        }
    }
}
