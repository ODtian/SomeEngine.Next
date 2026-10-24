using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Typography;
using SomeEngine.Views;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements.Render.Tests;

public sealed class ParallelExtractionTests(SurfaceExtractionDevice graphics) : IClassFixture<SurfaceExtractionDevice>
{
    [Theory]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(97)]
    [InlineData(2026)]
    public void ChunkJobsDeliverTheSameCanvasSurfaceImageAndGlyphBuffers(int seed)
    {
        using Font font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        using var serial = new DrawingFrames(font, parallel: false);
        using var parallel = new DrawingFrames(font, parallel: true);
        for (int frame = 0; frame < 10; frame++)
        {
            serial.Edit(seed + frame, frame);
            parallel.Edit(seed + frame, frame);
            serial.Update(graphics);
            parallel.Update(graphics);
            foreach (Entity canvas in serial.Canvases)
            {
                Entity expected = serial.Extraction.RequireMirror(canvas);
                Entity actual = parallel.Extraction.RequireMirror(canvas);
                Assert.Equal(serial.Render.Read<RenderCanvas>(expected), parallel.Render.Read<RenderCanvas>(actual));
                Assert.Equal(Read<SurfaceInstance>(serial.Render, expected), Read<SurfaceInstance>(parallel.Render, actual));
                Assert.Equal(Read<ImageInstance>(serial.Render, expected), Read<ImageInstance>(parallel.Render, actual));
                Assert.Equal(Read<GlyphInstance>(serial.Render, expected), Read<GlyphInstance>(parallel.Render, actual));
            }
            RenderGlyphAtlas expectedAtlas = serial.Render.Read<RenderGlyphAtlas>(serial.Extraction.RequireMirror(serial.Atlas));
            RenderGlyphAtlas actualAtlas = parallel.Render.Read<RenderGlyphAtlas>(parallel.Extraction.RequireMirror(parallel.Atlas));
            Assert.Equal(expectedAtlas.Version, actualAtlas.Version);
            Assert.Equal(expectedAtlas.DirtyRegions.ToArray(), actualAtlas.DirtyRegions.ToArray());
            Assert.Equal(expectedAtlas.Pages.Length, actualAtlas.Pages.Length);
            for (int page = 0; page < expectedAtlas.Pages.Length; page++)
                Assert.Equal(expectedAtlas.Pages.Span[page].Pixels.ToArray(), actualAtlas.Pages.Span[page].Pixels.ToArray());
        }
    }

    private static T[] Read<T>(RenderWorld world, Entity canvas) where T : struct, IBufferElement
    {
        T[] values = [];
        world.ExecuteBufferRead<T>(canvas, buffer => values = buffer.AsSpan().ToArray());
        return values;
    }

    private sealed class DrawingFrames : IDisposable
    {
        public World World { get; } = new();
        public RenderWorld Render { get; } = new();
        public RenderExtractionSystems Extraction { get; }
        public Entity Atlas { get; }
        public Entity[] Canvases { get; } = new Entity[4];
        private readonly Entity[] _nodes = new Entity[128];
        private readonly SystemGroup<ImmediateSystemContext> _systems;

        public DrawingFrames(Font font, bool parallel)
        {
            _systems = new(new ImmediateSystemDriver(World));
            var shaping = new TextShapingSystem { Parallel = parallel };
            _systems.Add(new ViewRebuildSystem());
            _systems.Add(shaping);
            _systems.Add(new LayoutSystem(shaping) { Parallel = parallel });
            Atlas = World.CreateEntity(new TextAtlas(new GlyphAtlas(128, 128)));
            Extraction = new(Render, new CanvasRenderExtractor { Parallel = parallel },
                new GlyphRenderExtractor(Atlas) { Parallel = parallel });
            for (int index = 0; index < Canvases.Length; index++)
            {
                Canvases[index] = World.CreateEntity(new Canvas { Size = new Size(320, 240) });
                World.Add(Canvases[index], new TextTheme(new TextStyle { Font = font, Size = 18 }));
            }
            for (int index = 0; index < _nodes.Length; index++)
            {
                Entity node = Views.Views.Spawn(World, Canvases[index % 4], new Text($"office AV {index}"));
                _nodes[index] = node;
                World.Add(node, new Surface { Fill = new Color(0.3f, 0.6f, 0.9f), CornerRadius = 4 });
                if (index % 2 == 0) World.Add(node, new Image(default, ImageFit.Contain));
                World.Add(node, new ElementTransform { Matrix = Matrix3x2.CreateScale(0.8f), Alpha = 0.7f });
            }
        }

        public void Edit(int seed, int frame)
        {
            var random = new Random(seed);
            foreach (Entity node in _nodes)
            {
                if (!World.IsAlive(node)) continue;
                if (random.Next(4) == 0) node.SetText(World, $"office {random.Next(10000)} next");
                if (random.Next(5) == 0)
                    World.Replace(node, new Surface { Fill = new Color((float)random.NextDouble(), 0.4f, 0.8f),
                        CornerRadius = random.Next(8), Shadow = new DropShadow(new Color(0, 0, 0, 0.5f), new Vector2(2, 3), 4) });
            }
            if (frame == 3) Hierarchy<ElementTree>.SetParent(World, _nodes[0], Canvases[1]);
            if (frame == 5) Hierarchy<ElementTree>.Reorder(World, _nodes[1], 0);
            if (frame == 7) Hierarchy<ElementTree>.DestroySubtree(World, _nodes[2]);
        }

        public void Update(SurfaceExtractionDevice graphics)
        {
            _systems.Update();
            Extraction.Extract(World);
            graphics.Apply(Extraction, Render);
        }
        public void Dispose() { Extraction.Dispose(); _systems.Dispose(); Render.Dispose(); World.Dispose(); }
    }
}
