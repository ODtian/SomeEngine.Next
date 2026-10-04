using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Elements;
using SomeEngine.Elements.Render;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using static SomeEngine.Layout.FlexFunctions;
using static SomeEngine.Elements.TextFunctions;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Runtime;

/// <summary>A screen panel made from ordinary views, layout and Surface modifiers.</summary>
internal sealed partial class SurfacePanelExample : IDisposable
{
    private readonly World _world;
    private readonly Entity _canvas;
    private readonly Entity _view;
    private readonly Font _font;
    internal Entity AtlasSource { get; }

    internal SurfacePanelExample(World world, int width, int height)
    {
        _world = world;
        using Stream fontStream = typeof(SurfacePanelExample).Assembly.GetManifestResourceStream(
            "SomeEngine.Runtime.Fonts.Example.otf")!;
        using var fontBytes = new MemoryStream();
        fontStream.CopyTo(fontBytes);
        _font = Font.Load(fontBytes.ToArray());
        AtlasSource = world.CreateEntity(new TextAtlas(new GlyphAtlas()));
        _canvas = world.CreateEntity(new Canvas { Size = new Size(width, height) });
        _view = Views.Views.Spawn(world, _canvas, new PanelView(width, height, _font));
    }

    internal void Resize(int width, int height)
    {
        Size size = new(width, height);
        if (_world.Read<Canvas>(_canvas).Size == size) return;
        _world.Replace(_canvas, new Canvas { Size = size });
        _world.Replace(_view, new PanelView(width, height, _font));
    }

    public void Dispose()
    {
        Hierarchy<ElementTree>.DestroySubtree(_world, _canvas);
        _world.DestroyEntity(AtlasSource);
        _font.Dispose();
    }

    private readonly record struct PanelView(int Width, int Height, Font Font) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (Column(10).Size(280, 160).Position(Math.Max(24, Width - 304), Math.Max(24, Height - 184))
                .Padding(18).Background(new Color(0.035f, 0.065f, 0.13f, 0.95f)).Corner(22)
                .Border(new Color(0.12f, 0.7f, 0.86f), 2)
                .Provide(new TextTheme(new TextStyle { Font = Font, Size = 22 })).Content())
            {
                Text("Hello SomeEngine").TextColor(new Color(0.12f, 0.7f, 0.86f));
                Text("中文你好世界");
                Row().Size(128, 14).Background(new Color(0.94f, 0.38f, 0.2f)).Corner(7);
            }
            return default;
        }
    }
}
