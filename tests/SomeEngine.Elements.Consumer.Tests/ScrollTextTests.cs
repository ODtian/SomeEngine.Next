using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class BehaviorTests
{
    [Fact]
    public void TextShapingReceivesTheScrollContentsNaturalMeasurementConstraints()
    {
        using Font font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 120) });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var shaping = new TextShapingSystem();
        systems.Add(new HitTestSystem());
        systems.Add(new ViewRebuildSystem());
        systems.Add(shaping);
        systems.Add(new LayoutSystem(shaping));
        Entity root = Views.Views.Spawn(world, canvas, new ScrollTextScene(font));
        systems.Update();
        Entity container = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        Entity scroll = Hierarchy<ElementTree>.GetChildren(world, container)[0];
        Entity text = Hierarchy<ElementTree>.GetChildren(world, scroll)[0];
        ShapedText shape = world.Read<ShapedText>(text);
        Assert.Equal(23.98f, shape.Size.Width, 3);
        Assert.Equal(54.48f, shape.Size.Height, 3);
        Assert.Equal(2, shape.Shape.LineCount);
        ScrollOffset offset = world.Read<ScrollOffset>(scroll);
        Assert.Equal(3.98f, offset.Max.X, 3);
        Assert.Equal(24.48f, offset.Max.Y, 3);

        Pointer.Move(world, canvas, new Vector2(5, 5));
        Pointer.Wheel(world, canvas, new Vector2(100, 100));
        systems.Update();
        offset = world.Read<ScrollOffset>(scroll);
        Assert.Equal(3.98f, offset.Value.X, 3);
        Assert.Equal(24.48f, offset.Value.Y, 3);
        Assert.Equal(-3.98f, world.Read<LayoutRect>(text).Value.X, 3);
        Assert.Equal(-24.48f, world.Read<LayoutRect>(text).Value.Y, 3);
    }

    private readonly record struct ScrollTextScene(Font Font) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (ScrollableFunctions.Scrollable(Absolute().Size(20, 30)).Content())
                Text("AV\nAV").Font(Font).FontSize(20);
            return default;
        }
    }
}
