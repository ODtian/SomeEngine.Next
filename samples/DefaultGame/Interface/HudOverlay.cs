using System.Globalization;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Elements;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Elements.VirtualListFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;
using Size = SomeEngine.Layout.Size;

namespace DefaultGame;

/// <summary>The standard scene HUD: eight hundred labels, one hundred changing numeric views and a long virtual list.</summary>
public sealed partial class HudOverlay : IDisposable
{
    public const int StaticLabelCount = 800;
    public const int ChangingNumberCount = 100;
    public const int ListItemCount = 10000;
    private readonly World _world;
    private readonly ScreenText _text;
    private readonly bool _disposeText;
    private readonly Entity _frame;
    private readonly Entity _view;
    public Entity Canvas { get; }
    public Entity AtlasSource => _text.AtlasSource;

    public HudOverlay(World world, int width, int height) : this(world, width, height, new ScreenText(world))
        => _disposeText = true;

    internal HudOverlay(World world, int width, int height, ScreenText text)
    {
        _world = world;
        _text = text;
        _frame = world.CreateEntity(new HudFrame());
        Canvas = world.CreateEntity(new Canvas { Size = new Size(width, height) });
        _view = Views.Views.Spawn(world, Canvas, new OverlayView(width, height, text.Font, _frame));
    }

    public void Update(int frameNumber) => _world.Replace(_frame, new HudFrame(frameNumber));

    public void Resize(int width, int height)
    {
        Size size = new(width, height);
        if (_world.Read<Canvas>(Canvas).Size == size) return;
        _world.Replace(Canvas, new Canvas { Size = size });
        _world.Replace(_view, new OverlayView(width, height, _text.Font, _frame));
    }

    public void Dispose()
    {
        Hierarchy<ElementTree>.DestroySubtree(_world, Canvas);
        _world.DestroyEntity(_frame);
        if (_disposeText) _text.Dispose();
    }

    private readonly record struct HudFrame(int Number = 0) : IComponent;

    private readonly record struct OverlayView(int Width, int Height, Font Font, Entity Frame) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Provide(new TextTheme(new TextStyle { Font = Font, Size = 9 })).Content())
            {
                Text("SomeUI HUD").FontSize(18).Position(16, 16);
                for (int index = 0; index < StaticLabelCount; index++)
                    Text(index.ToString("D3", CultureInfo.InvariantCulture)).Size(18, 18)
                        .Position(16 + index % 40 * 18, 48 + index / 40 * 18)
                        .Background(new Color(0.02f, 0.05f, 0.1f, 0.65f));
                for (int index = 0; index < ChangingNumberCount; index++)
                    Scope.View(new NumberView(index, Frame));
                Text("10000 items").FontSize(18).Position(Math.Max(16, Width - 288), 16);
                Scope.View(new ListView(Width, Height));
            }
            return default;
        }
    }

    private readonly record struct NumberView(int Index, Entity Frame) : IView, IComponent
    {
        public BuildTask Build()
        {
            int number = Scope.Read<HudFrame>(Frame).Number % 1000;
            Text(string.Create(CultureInfo.InvariantCulture, $"N{Index:D2}:{number:D3}")).Size(36, 18)
                .Position(16 + Index % 20 * 36, 432 + Index / 20 * 18)
                .TextColor(new Color(1, 0.75f, 0.2f))
                .Background(new Color(0.02f, 0.05f, 0.1f, 0.65f));
            return default;
        }
    }

    private readonly record struct ListView(int Width, int Height) : IView, IComponent
    {
        public BuildTask Build()
        {
            using var content = VirtualList(24).Size(264, Math.Clamp(Height - 96, 0, 240))
                .Position(Math.Max(16, Width - 288), 48)
                .Background(new Color(0.02f, 0.05f, 0.1f, 0.8f)).Content();
            foreach (int index in Visible(content.Entity, ListItemCount))
                using (Scope.Key((ulong)index))
                    Text(string.Create(CultureInfo.InvariantCulture, $"Item {index:D5}")).FontSize(14);
            return default;
        }
    }
}
