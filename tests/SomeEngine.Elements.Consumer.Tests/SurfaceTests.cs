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

public sealed partial class SurfaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RebuildingWithEqualSurfaceValuesPreservesTheirWriteVersions(bool rewritten)
    {
        using var frames = new ElementFrames();
        Entity root = frames.Spawn(new ResizedPanel(120, rewritten));
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity panel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        uint nodeVersion = frames.World.WriteVersion<Surface>(panel);
        uint typeVersion = frames.World.ComponentWriteVersion<Surface>();
        Surface surface = frames.World.Read<Surface>(panel);
        Assert.Equal(new Rect(0, 0, 120, 80), frames.World.Read<LayoutRect>(panel).Value);

        frames.World.Replace(root, new ResizedPanel(200, rewritten));
        frames.Frame();

        Assert.Equal(panel, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container)));
        Assert.Equal(new Rect(0, 0, 200, 80), frames.World.Read<LayoutRect>(panel).Value);
        Assert.Equal(surface, frames.World.Read<Surface>(panel));
        Assert.Equal(nodeVersion, frames.World.WriteVersion<Surface>(panel));
        Assert.Equal(typeVersion, frames.World.ComponentWriteVersion<Surface>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedFillPreservesTheCornersAndBorderAndAdvancesWriteVersions(bool rewritten)
    {
        using var frames = new ElementFrames();
        Entity root = frames.Spawn(new RecoloredPanel(new Color(0.2f, 0.4f, 0.6f, 0.8f), rewritten));
        frames.Frame();
        Entity panel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint nodeVersion = frames.World.WriteVersion<Surface>(panel);
        uint typeVersion = frames.World.ComponentWriteVersion<Surface>();

        frames.World.Replace(root, new RecoloredPanel(new Color(0.1f, 0.3f, 0.5f, 0.7f), rewritten));
        frames.Frame();

        Assert.Equal(panel, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Surface surface = frames.World.Read<Surface>(panel);
        Assert.Equal(new Color(0.1f, 0.3f, 0.5f, 0.7f), surface.Fill);
        Assert.Equal(4, surface.CornerRadius);
        Assert.Equal(new Color(0.9f, 0.7f, 0.5f), surface.BorderColor);
        Assert.Equal(2, surface.BorderWidth);
        Assert.NotEqual(nodeVersion, frames.World.WriteVersion<Surface>(panel));
        Assert.NotEqual(typeVersion, frames.World.ComponentWriteVersion<Surface>());
    }

#if DEBUG
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SurfaceModifiersReportTheRequiredBuildScope(int modifier)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            switch (modifier)
            {
                case 0: Entity.Null.Background(new Color(1, 0, 0)); break;
                case 1: Entity.Null.Corner(4); break;
                case 2: Entity.Null.Border(new Color(0, 0, 1), 2); break;
            }
        });
        Assert.Contains("active view build", error.Message);
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurfaceModifiersComposeOnTheSameLayoutNode(bool rewritten)
    {
        using var frames = new ElementFrames();
        Entity root = frames.Spawn(new StyledPanel(rewritten));

        frames.Frame();

        Entity panel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(panel, frames.World.Read<PanelReference>(root).Value);
        Surface surface = frames.World.Read<Surface>(panel);
        Assert.Equal(new Color(0.2f, 0.4f, 0.6f, 0.8f), surface.Fill);
        Assert.Equal(4, surface.CornerRadius);
        Assert.Equal(new Color(0.9f, 0.7f, 0.5f), surface.BorderColor);
        Assert.Equal(2, surface.BorderWidth);
        Assert.Equal(new Rect(0, 0, 800, 600), frames.World.Read<LayoutRect>(panel).Value);
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, panel).ToArray());
    }

    [Fact]
    public void BackgroundWritesTheFillOnTheLayoutNode()
    {
        using var frames = new ElementFrames();
        Entity root = frames.Spawn(new FilledPanel(new Color(0.2f, 0.4f, 0.6f, 0.8f)));

        frames.Frame();

        Entity panel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Surface surface = frames.World.Read<Surface>(panel);
        Assert.Equal(new Color(0.2f, 0.4f, 0.6f, 0.8f), surface.Fill);
        Assert.Equal(0, surface.CornerRadius);
        Assert.Equal(default, surface.BorderColor);
        Assert.Equal(0, surface.BorderWidth);
        Assert.Equal(new Rect(0, 0, 800, 600), frames.World.Read<LayoutRect>(panel).Value);
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, panel).ToArray());
    }

    private readonly record struct FilledPanel(Color Fill) : IView, IComponent
    {
        public BuildTask Build()
        {
            Absolute().Background(Fill);
            return default;
        }
    }

    private readonly record struct StyledPanel(bool Rewritten) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity panel = Decorate(Absolute(), Rewritten);
            Scope.Write<PanelReference>(Scope.Entity) = new PanelReference(panel);
            return default;
        }
    }

    private readonly record struct ResizedPanel(float Width, bool Rewritten) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                Decorate(Absolute().Size(Width, 80), Rewritten);
            return default;
        }
    }

    private readonly record struct RecoloredPanel(Color Fill, bool Rewritten) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity panel = Absolute();
            if (Rewritten)
            {
                UserSurface.Corner(panel, 4);
                UserSurface.Border(panel, new Color(0.9f, 0.7f, 0.5f), 2);
                UserSurface.Background(panel, Fill);
            }
            else
            {
                panel.Corner(4).Border(new Color(0.9f, 0.7f, 0.5f), 2).Background(Fill);
            }
            return default;
        }
    }

    private static Entity Decorate(Entity panel, bool rewritten)
    {
        if (rewritten)
        {
            panel = UserSurface.Background(panel, new Color(0.2f, 0.4f, 0.6f, 0.8f));
            panel = UserSurface.Corner(panel, 4);
            return UserSurface.Border(panel, new Color(0.9f, 0.7f, 0.5f), 2);
        }
        return panel.Background(new Color(0.2f, 0.4f, 0.6f, 0.8f))
            .Corner(4).Border(new Color(0.9f, 0.7f, 0.5f), 2);
    }

    private readonly record struct PanelReference(Entity Value) : IComponent;
}

internal sealed class ElementFrames : IDisposable
{
    private readonly SystemGroup<ImmediateSystemContext> _systems;
    private readonly Entity _canvas;

    public ElementFrames()
    {
        World = new World();
        _canvas = World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        _systems.Add(new ViewRebuildSystem());
        _systems.Add(new LayoutSystem());
    }

    public World World { get; }

    public Entity Spawn<T>(in T view) where T : struct, IView, IEquatable<T> =>
        Views.Views.Spawn(World, _canvas, in view);

    public void Frame() => _systems.Update();

    public void Dispose()
    {
        _systems.Dispose();
        World.Dispose();
    }
}
