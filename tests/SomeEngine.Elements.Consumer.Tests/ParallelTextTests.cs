using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements.Consumer.Tests;

[Collection("Text allocations")]
public sealed class ParallelTextTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(97)]
    [InlineData(2026)]
    public void ChangedTextHasTheSameGlyphsLinesAndVersionsWithChunkJobs(int seed)
    {
        using Font font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        using var serial = new TextFrame(font, parallel: false);
        using var parallel = new TextFrame(font, parallel: true);
        var random = new Random(seed);
        for (int frame = 0; frame < 12; frame++)
        {
            for (int index = 0; index < serial.Nodes.Length; index++)
            {
                string text = $"office AV {random.Next(100000)}\nline {index}";
                serial.Nodes[index].SetText(serial.World, text);
                parallel.Nodes[index].SetText(parallel.World, text);
            }
            serial.Update();
            parallel.Update();
            for (int index = 0; index < serial.Nodes.Length; index++)
            {
                ShapedText expected = serial.World.Read<ShapedText>(serial.Nodes[index]);
                ShapedText actual = parallel.World.Read<ShapedText>(parallel.Nodes[index]);
                Assert.Equal(expected.Size, actual.Size);
                Assert.Equal(expected.Style, actual.Style);
                Assert.Equal(expected.Version, actual.Version);
                Assert.Equal(expected.Shape.Glyphs.ToArray(), actual.Shape.Glyphs.ToArray());
                Assert.Equal(expected.Shape.Lines.ToArray(), actual.Shape.Lines.ToArray());
            }
        }
    }

    private sealed class TextFrame : IDisposable
    {
        public World World { get; } = new();
        public Entity[] Nodes { get; } = new Entity[96];
        private readonly SystemGroup<ImmediateSystemContext> _systems;

        public TextFrame(Font font, bool parallel)
        {
            _systems = new(new ImmediateSystemDriver(World));
            var shaping = new TextShapingSystem { Parallel = parallel };
            _systems.Add(new ViewRebuildSystem());
            _systems.Add(shaping);
            _systems.Add(new LayoutSystem(shaping));
            for (int index = 0; index < Nodes.Length; index++)
            {
                Entity canvas = World.CreateEntity(new Canvas { Size = new Size(80 + index % 5 * 20, 200) });
                World.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 16 + index % 3,
                    Color = new Color(0.4f, 0.7f, 0.9f) }));
                Nodes[index] = Views.Views.Spawn(World, canvas, new Text("office AV"));
            }
        }

        public void Update() => _systems.Update();
        public void Dispose() { _systems.Dispose(); World.Dispose(); }
    }
}
