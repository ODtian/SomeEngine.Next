using System.Globalization;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Layout.FlexFunctions;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Public text behavior shared with the NativeAOT executable.</summary>
internal static partial class TextChecks
{
    internal static void Run()
    {
        using Font font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var shaping = new TextShapingSystem();
        systems.Add(new ViewRebuildSystem());
        systems.Add(shaping);
        systems.Add(new LayoutSystem(shaping));
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(320, 240) });
        world.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 20 }));
        Entity root = Views.Views.Spawn(world, canvas, new TextExample(font, 42));
        systems.Update();
        Entity column = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        var children = Hierarchy<ElementTree>.GetChildren(world, column);
        Entity first = children[0];
        Entity number = children[1];
        Entity formatted = children[2];
        Require(world.Read<ShapedText>(first).Shape.LineCount == 2, "Explicit newlines produce two lines.");
        Require(MathF.Abs(world.Read<ShapedText>(first).Size.Height - 54.48f) < 0.001f,
            "The Latin font at 20 pixels measures two lines at 54.48 pixels.");
        Require(MathF.Abs(world.Read<LayoutRect>(number).Value.Y - 59.48f) < 0.001f,
            "Column places the number after the preceding text and gap.");
        Require(world.Read<Text>(number).Value == "0042", "The integer declaration applies its format.");
        Require(world.Read<ShapedText>(number).Size.Height == 18,
            "The line-height modifier supplies a pixel height.");
        Require(world.Read<ShapedText>(number).Style.Color == new Color(0.2f, 0.4f, 0.6f, 0.8f),
            "The color modifier reaches the text result.");
        Require(world.Read<Text>(formatted).Value == "12.50", "Generic text formatting uses the supplied culture.");
        uint textVersion = world.WriteVersion<Text>(number);
        uint shapeVersion = world.WriteVersion<ShapedText>(number);
        uint layoutVersion = world.WriteVersion<LayoutRect>(number);
        world.Replace(root, new TextExample(font, 42));
        systems.Update();
        Require(world.WriteVersion<Text>(number) == textVersion && world.WriteVersion<ShapedText>(number) == shapeVersion &&
            world.WriteVersion<LayoutRect>(number) == layoutVersion, "Equal text preserves its text, shape, and layout versions.");
        for (int index = 0; index < 1000; index++)
            number.SetText(world, 42, "D4");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10000; index++)
            number.SetText(world, 42, "D4");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0 && world.WriteVersion<Text>(number) == textVersion,
            "Repeated integer writes allocate zero bytes and preserve the text version.");
        world.Replace(root, new TextExample(font, 43));
        systems.Update();
        Require(world.Read<Text>(number).Value == "0043" && world.WriteVersion<ShapedText>(number) != shapeVersion,
            "Changed integer parameters reach both text and shaping.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct TextExample(Font Font, int Number) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Column(5).Align(Align.Start, Align.Start).Content())
            {
                Text("AV\nAV").Font(Font);
                Text(Number, "D4").FontSize(10).TextColor(new Color(0.2f, 0.4f, 0.6f, 0.8f)).LineHeight(18);
                Text(12.5m, "F2", CultureInfo.InvariantCulture);
            }
            return default;
        }
    }
}
