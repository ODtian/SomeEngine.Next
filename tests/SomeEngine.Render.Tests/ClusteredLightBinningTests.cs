using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class ClusteredLightBinningTests
{
    [Fact]
    public void LightAssignmentsFollowEachViewIndependently()
    {
        using var context = new LightBinningContext(new()
        {
            TileSize = 8, DepthSliceCount = 8, MaxLights = 8, MaxLightsPerCell = 8, LightListCapacity = 4096,
        }, 2);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10_000);
        var near = new RenderView(Matrix4x4.Identity, projection, 64, 64);
        var distant = new RenderView(Matrix4x4.CreateTranslation(0, 0, -1_000_000), projection, 64, 64);
        var lights = new RenderLightSet();
        lights.AddPoint(new RenderPointLight(new(0, 0, -2), 100_000, Vector3.One, 1, uint.MaxValue));
        ulong identity;
        RenderGraphFrame frame = context.BeginFrame();
        try
        {
            context.Binning.Record(ref frame, lights, [near, distant], Vector3.Zero);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        uint[] cells = context.Cells.Values;
        uint[] indices = context.Indices.Values;
        for (uint cell = 0; cell < context.Binning.CellCountPerView; cell++)
        {
            Assert.Equal(1u, cells[cell * 2 + 1]);
            Assert.Equal(0u, indices[cells[cell * 2]]);
            Assert.Equal(0u, cells[(context.Binning.CellCountPerView + cell) * 2 + 1]);
        }
    }

    [Fact]
    public void PointLightCellsMatchViewSpaceSphereAndCellBoxIntersections()
    {
        var settings = new ClusteredLightGridSettings
        {
            TileSize = 8, DepthSliceCount = 8, MaxLights = 8, MaxLightsPerCell = 8, LightListCapacity = 4096,
        };
        using var context = new LightBinningContext(settings);
        var view = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10_000), 64, 64);
        var lights = new RenderLightSet();
        Vector3 position = new(0, 0, -2);
        lights.AddPoint(new RenderPointLight(position, 1, Vector3.One, 1, uint.MaxValue));
        ulong identity;
        RenderGraphFrame frame = context.BeginFrame();
        try
        {
            context.Binning.Record(ref frame, lights, [view], Vector3.Zero);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        ClusteredLightBinningView parameters = ClusteredLightBinningView.Create(view, settings, 0, 1);
        var expected = new List<uint>();
        var actual = new List<uint>();
        uint[] cells = context.Cells.Values;
        for (uint cell = 0; cell < context.Binning.CellCountPerView; cell++)
        {
            (Vector3 minimum, Vector3 maximum) = CellBounds(parameters, cell);
            Vector3 closest = Vector3.Clamp(position, minimum, maximum);
            if (Vector3.DistanceSquared(position, closest) <= 1f) expected.Add(cell);
            if (cells[cell * 2 + 1] != 0) actual.Add(cell);
        }
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LocalLightListsAreAscendingAndIdenticalAcrossFixedFrames()
    {
        using var context = new LightBinningContext(new()
        {
            TileSize = 8, DepthSliceCount = 8, MaxLights = 8, MaxLightsPerCell = 8, LightListCapacity = 4096,
        });
        var view = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10_000), 64, 64);
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(-Vector3.UnitZ, Vector3.One, 1, uint.MaxValue));
        lights.AddPoint(new RenderPointLight(new(0, 0, -2), 100_000, Vector3.One, 1, uint.MaxValue));
        lights.AddSpot(new RenderSpotLight(new(0, 0, -2), 100_000, -Vector3.UnitZ, .9f, .5f,
            Vector3.One, 4, uint.MaxValue));
        byte[]? firstCells = null;
        byte[]? firstIndices = null;
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            ulong identity;
            RenderGraphFrame frame = context.BeginFrame();
            try
            {
                context.Binning.Record(ref frame, lights, [view], Vector3.Zero);
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
            uint[] cells = context.Cells.Values;
            uint[] indices = context.Indices.Values;
            for (uint cell = 0; cell < context.Binning.CellCountPerView; cell++)
            {
                uint offset = cells[cell * 2];
                uint count = cells[cell * 2 + 1];
                for (uint index = 0; index < count; index++)
                {
                    Assert.InRange(indices[offset + index], 1u, 2u);
                    if (index != 0) Assert.True(indices[offset + index - 1] < indices[offset + index]);
                }
            }
            if (frameIndex == 0)
            {
                firstCells = context.Cells.Bytes;
                firstIndices = context.Indices.Bytes;
            }
            else
            {
                Assert.Equal(firstCells, context.Cells.Bytes);
                Assert.Equal(firstIndices, context.Indices.Bytes);
            }
        }
    }

    [Fact]
    public void PerCellLimitRetainsHighestPriorityLightsAndReportsTruncatedCells()
    {
        using var context = new LightBinningContext(new()
        {
            TileSize = 8, DepthSliceCount = 8, MaxLights = 8, MaxLightsPerCell = 2, LightListCapacity = 4096,
        });
        var view = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10_000), 64, 64);
        var lights = new RenderLightSet();
        for (int index = 0; index < 5; index++)
            lights.AddPoint(new RenderPointLight(new(0, 0, -2), 100_000, Vector3.One, index + 1, uint.MaxValue));
        ulong identity;
        RenderGraphFrame frame = context.BeginFrame();
        try
        {
            context.Binning.Record(ref frame, lights, [view], Vector3.Zero);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        uint[] cells = context.Cells.Values;
        uint[] indices = context.Indices.Values;
        for (uint cell = 0; cell < context.Binning.CellCountPerView; cell++)
        {
            Assert.Equal(2u, cells[cell * 2 + 1]);
            Assert.Equal(0u, indices[cells[cell * 2]]);
            Assert.Equal(1u, indices[cells[cell * 2] + 1]);
        }
        Assert.Equal(context.Binning.CellCountPerView, context.Binning.LastFrameOverflowCells);
    }

    [Fact]
    public void FramePreparationRestoresAssignmentsFromCapacityFeedback()
    {
        using var context = new LightBinningContext(new()
        {
            TileSize = 8, DepthSliceCount = 8, MaxLights = 8, MaxLightsPerCell = 8, LightListCapacity = 256,
        });
        var view = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10_000), 64, 64);
        var lights = new RenderLightSet();
        lights.AddPoint(new RenderPointLight(new(0, 0, -2), 100_000, Vector3.One, 1, uint.MaxValue));
        ulong identity;
        RenderGraphFrame frame = context.BeginFrame();
        try
        {
            context.Binning.Record(ref frame, lights, [view], Vector3.Zero);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        uint[] cells = context.Cells.Values;
        for (uint cell = 0; cell < context.Binning.CellCountPerView; cell++)
            Assert.Equal(cell < 256 ? 1u : 0u, cells[cell * 2 + 1]);
        Assert.Equal(256u, context.Binning.LastFrameOverflowCells);

        for (int feedbackFrame = 0; feedbackFrame < 2; feedbackFrame++)
        {
            frame = context.BeginFrame();
            try
            {
                context.Binning.Record(ref frame, lights, [view], Vector3.Zero);
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        }
        cells = context.Cells.Values;
        for (uint cell = 0; cell < context.Binning.CellCountPerView; cell++)
            Assert.Equal(1u, cells[cell * 2 + 1]);
        Assert.Equal(0u, context.Binning.LastFrameOverflowCells);
        Assert.Equal(identity, context.Binning.FeedbackFrameIdentity);
    }

    private static (Vector3 Minimum, Vector3 Maximum) CellBounds(ClusteredLightBinningView view, uint cell)
    {
        uint x = cell % view.TileCountX;
        uint y = cell / view.TileCountX % view.TileCountY;
        uint z = cell / (view.TileCountX * view.TileCountY);
        float near = view.ZParams.X * MathF.Pow(2, z / view.ZParams.Z);
        float far = view.ZParams.X * MathF.Pow(2, (z + 1) / view.ZParams.Z);
        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            float depth = (corner & 4) == 0 ? near : far;
            float pixelX = Math.Min((x + ((corner & 1) == 0 ? 0u : 1u)) * view.TileSizeX, view.ScreenWidth);
            float pixelY = Math.Min((y + ((corner & 2) == 0 ? 0u : 1u)) * view.TileSizeY, view.ScreenHeight);
            float ndcX = pixelX / view.ScreenWidth * 2 - 1;
            float ndcY = 1 - pixelY / view.ScreenHeight * 2;
            Vector3 point = new(ndcX * depth / view.Projection.M11, ndcY * depth / view.Projection.M22, -depth);
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }
        return (minimum, maximum);
    }

    private sealed class LightBinningContext : IDisposable
    {
        private readonly BinningTestDevice _graphics = new(DeviceFeatures.IndirectCommands);
        private readonly RenderWorld _world = new();
        private readonly RenderExtractionSystems _extraction;
        private readonly RenderPrepareSystems _prepare;
        private readonly RenderGraphFrameOptions _frameOptions = default;
        private readonly ShaderLibrary _shaders;
        private readonly GraphReadbackId _cellReadback;
        private readonly GraphReadbackId _indexReadback;
        internal global::SomeEngine.RenderGraph.RenderGraph Graph { get; }
        internal ClusteredLightBinning Binning { get; }
        internal ReadbackBytes Cells { get; } = new();
        internal ReadbackBytes Indices { get; } = new();

        internal LightBinningContext(ClusteredLightGridSettings settings, uint viewCount = 1)
        {
            _extraction = new(_world);
            _prepare = new(_world, _extraction);
            string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/Shaders"));
            IGlobalSession global = Shader.CreateSlangGlobalSession();
            SessionDesc compilation = TestShaderLoading.CreateSessionDescription(global);
            compilation.SearchPaths = [directory];
            _shaders = new(global, compilation);
            Shader sharedShader = _shaders.CreateAsync("Shared screen binning", "screen_binning.slang",
                File.ReadAllText(Path.Combine(directory, "screen_binning.slang"))).AsTask().GetAwaiter().GetResult();
            Shader shader = _shaders.CreateAsync("Clustered light binning", "clustered_light_binning.slang",
                File.ReadAllText(Path.Combine(directory, "clustered_light_binning.slang"))).AsTask().GetAwaiter().GetResult();
            Graph = new(_graphics.Backend, _graphics.Device,
                [_graphics.Backend.GetQueue(_graphics.Device, QueueType.Graphics)],
                new RenderGraphDesc(MaximumFramesInFlight: 2));
            Binning = new(Graph, _graphics.Pipelines, _prepare, 64, 64, viewCount, sharedShader, shader, settings);
            using RenderGraphEdit edit = Graph.BeginEdit();
            _cellReadback = edit.AddReadback(Binning.Cells, BufferRange.Whole, Cells, "Light cell readback");
            _indexReadback = edit.AddReadback(Binning.LightIndices, BufferRange.Whole, Indices, "Light index readback");
            edit.OrderAfter(_cellReadback, Binning.CompletePass);
            edit.OrderAfter(_indexReadback, Binning.CompletePass);
            edit.Commit();
        }

        internal RenderGraphFrame BeginFrame()
        {
            Assert.True(Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.FromSeconds(10)));
            using (next)
            {
                _prepare.Update();
                return Graph.BeginFrame(next, in _frameOptions);
            }
        }

        public void Dispose()
        {
            using (RenderGraphEdit edit = Graph.BeginEdit())
            {
                edit.Remove(_cellReadback);
                edit.Remove(_indexReadback);
                edit.Commit();
            }
            Binning.Dispose();
            _prepare.Dispose();
            _extraction.Dispose();
            _world.Dispose();
            Graph.Dispose();
            _shaders.Dispose();
            _graphics.Dispose();
        }
    }

    private sealed class ReadbackBytes : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        internal uint[] Values => MemoryMarshal.Cast<byte, uint>(Bytes).ToArray();
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }
}
