using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Elements;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using static SomeEngine.Layout.FlexFunctions;

namespace SomeEngine.Runtime;

/// <summary>A screen panel made from ordinary views, layout and Surface modifiers.</summary>
internal sealed partial class SurfacePanelExample : IDisposable
{
    private readonly World _world;
    private readonly Entity _canvas;
    private readonly Entity _view;

    internal SurfacePanelExample(World world, int width, int height)
    {
        _world = world;
        _canvas = world.CreateEntity(new Canvas { Size = new Size(width, height) });
        _view = Views.Views.Spawn(world, _canvas, new PanelView(width, height));
    }

    internal void Resize(int width, int height)
    {
        Size size = new(width, height);
        if (_world.Read<Canvas>(_canvas).Size == size) return;
        _world.Replace(_canvas, new Canvas { Size = size });
        _world.Replace(_view, new PanelView(width, height));
    }

    public void Dispose()
    {
        Hierarchy<ElementTree>.DestroySubtree(_world, _canvas);
    }

    private readonly record struct PanelView(int Width, int Height) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (Column().Size(280, 160).Position(Math.Max(24, Width - 304), Math.Max(24, Height - 184))
                .Padding(18).Background(new Color(0.035f, 0.065f, 0.13f, 0.95f)).Corner(22)
                .Border(new Color(0.12f, 0.7f, 0.86f), 2).Content())
            {
                Row().Size(224, 28).Background(new Color(0.12f, 0.7f, 0.86f)).Corner(8);
                Row().Size(128, 48).Background(new Color(0.94f, 0.38f, 0.2f)).Corner(12);
            }
            return default;
        }
    }
}
