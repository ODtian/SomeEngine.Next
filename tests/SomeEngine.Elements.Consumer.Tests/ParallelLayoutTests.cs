using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using Size = SomeEngine.Layout.Size;
using System.Numerics;

namespace SomeEngine.Elements.Consumer.Tests;

[Collection("Text allocations")]
public sealed partial class ParallelLayoutTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(97)]
    [InlineData(2026)]
    public void CanvasAndBoundaryJobsMatchSequentialLayoutAfterTreeAndContentChanges(int seed)
    {
        using Font font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        using var serial = new Frames(font, seed, parallel: false);
        using var parallel = new Frames(font, seed, parallel: true);
        for (int frame = 0; frame < 16; frame++)
        {
            serial.Edit(seed + frame, frame);
            parallel.Edit(seed + frame, frame);
            serial.Update();
            parallel.Update();
            Entity[] expectedNodes = serial.Nodes();
            Entity[] actualNodes = parallel.Nodes();
            Assert.Equal(expectedNodes, actualNodes);
            foreach (Entity node in expectedNodes)
            {
                Assert.Equal(serial.World.Has<LayoutRect>(node), parallel.World.Has<LayoutRect>(node));
                if (serial.World.Has<LayoutRect>(node))
                    Assert.Equal(serial.World.Read<LayoutRect>(node), parallel.World.Read<LayoutRect>(node));
                if (!serial.World.Has<ShapedText>(node)) continue;
                ShapedText expected = serial.World.Read<ShapedText>(node);
                ShapedText actual = parallel.World.Read<ShapedText>(node);
                Assert.Equal(expected.Size, actual.Size);
                Assert.Equal(expected.Version, actual.Version);
                Assert.Equal(expected.Shape.Glyphs.ToArray(), actual.Shape.Glyphs.ToArray());
                Assert.Equal(expected.Shape.Lines.ToArray(), actual.Shape.Lines.ToArray());
            }
        }
    }

    private sealed class Frames : IDisposable
    {
        public World World { get; } = new();
        private readonly SystemGroup<ImmediateSystemContext> _systems;
        private readonly Entity[] _canvases = new Entity[4];

        public Frames(Font font, int seed, bool parallel)
        {
            _systems = new(new ImmediateSystemDriver(World));
            var shaping = new TextShapingSystem { Parallel = parallel };
            _systems.Add(new ViewRebuildSystem());
            _systems.Add(shaping);
            _systems.Add(new LayoutSystem(shaping) { Parallel = parallel });
            for (int index = 0; index < _canvases.Length; index++)
            {
                Entity canvas = World.CreateEntity(new Canvas { Size = new Size(500, 400) });
                _canvases[index] = canvas;
                World.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 16 }));
                Views.Views.Spawn(World, canvas, new Tree(seed + index));
            }
        }

        public Entity[] Nodes() => _canvases.SelectMany(canvas => Descendants(canvas)).ToArray();
        private IEnumerable<Entity> Descendants(Entity node)
        {
            yield return node;
            foreach (Entity child in Hierarchy<ElementTree>.GetChildren(World, node))
                foreach (Entity descendant in Descendants(child)) yield return descendant;
        }

        public void Edit(int seed, int frame)
        {
            var random = new Random(seed);
            Entity[] nodes = Nodes();
            foreach (Entity node in nodes)
            {
                if (World.Has<Text>(node) && random.Next(3) == 0)
                    node.SetText(World, $"office {random.Next(100000)} AV\nnext line");
                if (World.Has<Box>(node) && random.Next(6) == 0)
                {
                    Box box = World.Read<Box>(node);
                    box.Fixed = random.Next(2) == 0 ? new Size(random.Next(60, 180), random.Next(40, 100)) : Size.Auto;
                    World.Replace(node, box);
                }
                if (World.Has<Flex>(node) && random.Next(7) == 0)
                    World.Replace(node, new Flex((Axis)random.Next(2), random.Next(6), CrossAlign: Align.Start));
                if (frame % 5 == 4 && World.Has<LayoutCache>(node)) World.Remove<LayoutCache>(node);
            }
            if (frame % 3 == 1)
            {
                Entity parent = nodes.FirstOrDefault(node => World.Has<Flex>(node));
                var children = Hierarchy<ElementTree>.GetChildren(World, parent);
                if (children.Count > 1) Hierarchy<ElementTree>.Reorder(World, children[^1], 0);
            }
            if (frame % 4 == 2)
                World.Replace(_canvases[0], new Canvas { Size = new Size(420 + random.Next(160), 400), Scale = 2 });
        }

        public void Update() => _systems.Update();
        public void Dispose() { _systems.Dispose(); World.Dispose(); }
    }

    private readonly record struct Tree(int Seed) : IView, IComponent
    {
        public BuildTask Build() { Branch(new Random(Seed), 3, 1); return default; }

        private static void Branch(Random random, int depth, ulong key)
        {
            if (depth == 0)
            {
                using (Scope.View(new CachedPlacement(), key).Content())
                    Scope.View(new Text($"office {random.Next(1000)} AV"), 1).Padding(2);
                return;
            }
            Entity node = Scope.View(new Flex((Axis)random.Next(2), random.Next(4), CrossAlign: Align.Start), key);
            node.Padding(2).Grow(1);
            if (depth == 1) node.Size(120, 80);
            using (node.Content())
                for (ulong child = 1; child <= 4; child++) Branch(random, depth - 1, child);
        }
    }

    private readonly record struct CachedPlacement : IView, IComponent, ILayout
    {
        public static ChildSource Children => ChildSource.Caller;
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            Size extent = default;
            foreach (Entity child in pass.Children)
            {
                pass.Measure(child, in constraints);
                Size size = pass.Read<LayoutCache>(child).Size;
                pass.Place(child, new Vector2(size.Width, 0));
                extent = new Size(MathF.Max(extent.Width, size.Width * 2), MathF.Max(extent.Height, size.Height));
            }
            return constraints.Constrain(extent);
        }
    }
}
