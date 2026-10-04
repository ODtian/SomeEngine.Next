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
        VerifyTextSources();
        VerifyRepeatedDeclarations();
        foreach (string example in new[] { "decimal", "offset", "kind", "double", "float", "half" })
            VerifyChangedFormattingData(example);
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

    internal static void VerifyTextSources()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity root = Views.Views.Spawn(world, Entity.Null, new WrittenNumber());
        systems.Update();
        Entity node = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        Require(world.Read<Text>(node).Value == "42" && world.Read<TextContent>(node).Value == "42",
            "The scoped numeric write switches both the parameters and displayed text to its new value.");
        node.SetText(world, 99, "D2");
        systems.Update();
        Require(world.Read<Text>(node).Value == "99" && world.Read<TextContent>(node).Value == "99",
            "The World numeric write uses the same text-source rule.");
        uint version = world.WriteVersion<Text>(node);
        node.SetText(world, 99, "D2");
        node.SetText(world, "99");
        systems.Update();
        Require(world.WriteVersion<Text>(node) == version, "Equal caller-written text retains its version.");
    }

    private readonly record struct WrittenNumber : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;
        public BuildTask Build()
        {
            Entity node = DeclareText(1, 23, "D2");
            node.SetText(42, "D2");
            return default;
        }
    }

    internal static void VerifyRepeatedDeclarations()
    {
        long strings = MeasureDeclarations(0);
        for (int kind = 1; kind <= 3; kind++)
        {
            long numbers = MeasureDeclarations(kind);
            Require(numbers == strings,
                $"Repeated formatted declarations ({kind}) allocate the same bytes as equal string declarations: {strings} / {numbers}.");
        }
    }

    private static long MeasureDeclarations(int kind)
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        var change = new State<int>(0);
        var allocation = new DeclarationAllocation();
        Entity root = Views.Views.Spawn(world, Entity.Null, new RepeatedDeclaration(change, kind, allocation));
        for (int frame = 1; frame <= 10000; frame++)
        {
            change.Value = frame;
            systems.Update();
        }
        Entity node = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        uint version = world.WriteVersion<Text>(node);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        for (int frame = 1; frame <= 1000; frame++)
        {
            change.Value = 10000 + frame;
            systems.Update();
        }
        Require(world.WriteVersion<Text>(node) == version, "Equal declarations retain the text write version.");
        Require(world.Read<TextContent>(node).Value == "0042", "Every declaration presents the same four characters.");
        return allocation.Bytes;
    }

    private sealed class DeclarationAllocation
    {
        internal long Bytes;
    }

    private readonly record struct RepeatedDeclaration(State<int> Change, int Kind, DeclarationAllocation Allocation) : IView, IComponent
    {
        public BuildTask Build()
        {
            int frame = Change.Value;
            long before = GC.GetAllocatedBytesForCurrentThread();
            switch (Kind)
            {
                case 1:
                    DeclareText(1, 42, "D4");
                    break;
                case 2:
                    DeclareText(1, 42m, "0000", CultureInfo.InvariantCulture);
                    break;
                case 3:
                    DeclareText(1, new DateTime(2026, 10, 4), "'0042'", CultureInfo.InvariantCulture);
                    break;
                default:
                    Scope.View(new Text("0042"), 1);
                    break;
            }
            if (frame > 10000)
                Allocation.Bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            return default;
        }
    }

    internal static void VerifyChangedFormattingData(string example)
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity root = Views.Views.Spawn(world, Entity.Null, new ChangedFormattingData(example, false));
        systems.Update();
        Entity node = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        world.Replace(root, new ChangedFormattingData(example, true));
        systems.Update();
        string expected = example switch
        {
            "decimal" => "1.0",
            "offset" => "14:00",
            "kind" => "2026-10-04T12:00:00.0000000Z",
            _ => "0.00",
        };
        Require(world.Read<Text>(node).Value == expected && world.Read<TextContent>(node).Value == expected,
            $"Changed {example} formatting data reaches both the parameters and displayed content.");
    }

    private readonly record struct ChangedFormattingData(string Example, bool Changed) : IView, IComponent
    {
        public BuildTask Build()
        {
            switch (Example)
            {
                case "decimal":
                    DeclareText(1, Changed ? 1.0m : 1m, "G", CultureInfo.InvariantCulture);
                    break;
                case "offset":
                    DateTimeOffset date = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
                    DeclareText(1, Changed ? date.ToOffset(TimeSpan.FromHours(2)) : date, "HH:mm", CultureInfo.InvariantCulture);
                    break;
                case "kind":
                    DeclareText(1, new DateTime(2026, 10, 4, 12, 0, 0,
                        Changed ? DateTimeKind.Utc : DateTimeKind.Unspecified), "O", CultureInfo.InvariantCulture);
                    break;
                case "double":
                    DeclareText(1, Changed ? 0d : BitConverter.Int64BitsToDouble(long.MinValue), "F2", CultureInfo.InvariantCulture);
                    break;
                case "float":
                    DeclareText(1, Changed ? 0f : BitConverter.Int32BitsToSingle(int.MinValue), "F2", CultureInfo.InvariantCulture);
                    break;
                case "half":
                    DeclareText(1, Changed ? (Half)0 : BitConverter.Int16BitsToHalf(short.MinValue), "F2", CultureInfo.InvariantCulture);
                    break;
            }
            return default;
        }
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
