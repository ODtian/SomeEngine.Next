using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Decals;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class MaterialDecalBinningTests
{
    private readonly RenderGraphFrameOptions _frameOptions = default;

    [Fact]
    public async Task GrowingDecalWorkKeepsTheRegisteredCellAndReferenceConnections()
    {
        using var graphics = new BinningTestDevice(DeviceFeatures.IndirectCommands);
        using var shaders = await DecalShaders.CreateAsync();
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        using var prepare = new RenderPrepareSystems(world, extraction);
        var settings = new MaterialDecalBinningSettings { TileSize = 16, DepthSliceCount = 2, NearDepth = 1, FarDepth = 16 };
        RenderView[] views = [new(Matrix4x4.Identity, Matrix4x4.CreateOrthographic(8, 8, 1, 16), 64, 64)];
        var volumes = Enumerable.Repeat(Matrix4x4.CreateScale(.2f) * Matrix4x4.CreateTranslation(-1, 1, -2.5f), 16).ToArray();
        var cells = new ReadbackBytes();
        var indices = new ReadbackBytes();
        var decals = new MaterialDecalBinning(graph, graphics.Pipelines, prepare, shaders.Shared, shaders.Decals,
            64, 64, 1, 1, 32, settings);
        GraphBufferId cellBuffer = decals.Cells, indexBuffer = decals.Indices, diagnostics = decals.Diagnostics;
        GraphRegionId region = decals.Region;
        GraphReadbackId cellReadback, indexReadback;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            cellReadback = edit.AddReadback(cellBuffer, new(0, 32 * 8), cells, "Read grown decal cells");
            indexReadback = edit.AddReadback(indexBuffer, new(0, 32 * 4), indices, "Read grown decal references");
            edit.OrderAfter(cellReadback, decals.CompletePass);
            edit.OrderAfter(indexReadback, decals.CompletePass);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        try
        {
            decals.SetCapacity(16);

            Assert.Equal(cellBuffer, decals.Cells);
            Assert.Equal(indexBuffer, decals.Indices);
            Assert.Equal(diagnostics, decals.Diagnostics);
            Assert.Equal(region, decals.Region);
            RenderGraphFrame frame = BeginFrame(graph, prepare);
            ulong frameNumber;
            try
            {
                decals.Record(ref frame, volumes, views);
                frameNumber = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(frameNumber, TimeSpan.FromSeconds(10)));
            uint[] ranges = MemoryMarshal.Cast<byte, uint>(cells.Bytes).ToArray();
            uint[] references = MemoryMarshal.Cast<byte, uint>(indices.Bytes).ToArray();
            Assert.Equal(16u, ranges[5 * 2 + 1]);
            Assert.Equal(Enumerable.Range(0, 16).Select(index => (uint)index), references.Skip((int)ranges[5 * 2]).Take(16));
        }
        finally
        {
            RemoveReadbacks(graph, cellReadback, indexReadback);
            decals.Dispose();
        }
    }

    [Fact]
    public async Task RotatedDecalVolumesEnterTheIntersectingCellsOfEachView()
    {
        using var graphics = new BinningTestDevice(DeviceFeatures.IndirectCommands);
        using var shaders = await DecalShaders.CreateAsync();
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        using var prepare = new RenderPrepareSystems(world, extraction);
        var settings = new MaterialDecalBinningSettings { TileSize = 16, DepthSliceCount = 2, NearDepth = 1, FarDepth = 16 };
        Matrix4x4 projection = Matrix4x4.CreateOrthographic(8, 8, 1, 16);
        RenderView[] views = [new(Matrix4x4.Identity, projection, 64, 64), new(Matrix4x4.CreateTranslation(-2, 0, 0), projection, 64, 64)];
        Matrix4x4[] volumes = [Matrix4x4.CreateScale(.45f, .6f, .25f) * Matrix4x4.CreateRotationZ(MathF.PI / 4)
            * Matrix4x4.CreateTranslation(-1, 1, -2.5f)];
        var cells = new ReadbackBytes();
        var indices = new ReadbackBytes();
        var decals = new MaterialDecalBinning(graph, graphics.Pipelines, prepare, shaders.Shared, shaders.Decals,
            64, 64, 2, 1, 64, settings);
        GraphReadbackId cellReadback, indexReadback;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            cellReadback = edit.AddReadback(decals.Cells, new(0, 64 * 8), cells, "Read decal cells");
            indexReadback = edit.AddReadback(decals.Indices, new(0, 64 * 4), indices, "Read decal references");
            edit.OrderAfter(cellReadback, decals.CompletePass);
            edit.OrderAfter(indexReadback, decals.CompletePass);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        try
        {
            RenderGraphFrame frame = BeginFrame(graph, prepare);
            ulong frameNumber;
            try
            {
                decals.Record(ref frame, volumes, views);
                frameNumber = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(frameNumber, TimeSpan.FromSeconds(10)));

            Assert.Equal(32u, decals.CellCountPerView);
            uint[] ranges = MemoryMarshal.Cast<byte, uint>(cells.Bytes).ToArray();
            uint[] references = MemoryMarshal.Cast<byte, uint>(indices.Bytes).ToArray();
            var occupied = new List<int>();
            for (int cell = 0; cell < 64; cell++)
            {
                uint count = ranges[cell * 2 + 1];
                if (count == 0) continue;
                occupied.Add(cell);
                Assert.Equal(1u, count);
                Assert.Equal(0u, references[ranges[cell * 2]]);
            }
            // Each screen tile is two world units wide; the two camera positions place the box in distinct tiles.
            Assert.Equal(new[] { 5, 36 }, occupied);
            Assert.Equal(0u, decals.LastFrameOverflowCells);
        }
        finally
        {
            RemoveReadbacks(graph, cellReadback, indexReadback);
            decals.Dispose();
        }
    }

    [Fact]
    public async Task OverlappingDecalsKeepApplicationOrderAcrossFrames()
    {
        using var graphics = new BinningTestDevice(DeviceFeatures.IndirectCommands);
        using var shaders = await DecalShaders.CreateAsync();
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        using var prepare = new RenderPrepareSystems(world, extraction);
        var settings = new MaterialDecalBinningSettings { TileSize = 16, DepthSliceCount = 2, NearDepth = 1, FarDepth = 16 };
        RenderView[] views = [new(Matrix4x4.Identity, Matrix4x4.CreateOrthographic(8, 8, 1, 16), 64, 64)];
        var volumes = new Matrix4x4[16];
        for (int index = 0; index < volumes.Length; index++)
            volumes[index] = Matrix4x4.CreateScale(.4f, .5f, .2f) * Matrix4x4.CreateRotationZ(index * .17f)
                * Matrix4x4.CreateTranslation(-1, 1, -2.5f);
        var cells = new ReadbackBytes();
        var indices = new ReadbackBytes();
        var decals = new MaterialDecalBinning(graph, graphics.Pipelines, prepare, shaders.Shared, shaders.Decals,
            64, 64, 1, 16, 32, settings);
        GraphReadbackId cellReadback, indexReadback;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            cellReadback = edit.AddReadback(decals.Cells, new(0, 32 * 8), cells, "Read ordered decal cells");
            indexReadback = edit.AddReadback(decals.Indices, new(0, 32 * 4), indices, "Read ordered decal references");
            edit.OrderAfter(cellReadback, decals.CompletePass);
            edit.OrderAfter(indexReadback, decals.CompletePass);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        try
        {
            byte[] firstCells = [], firstIndices = [];
            for (int iteration = 0; iteration < 2; iteration++)
            {
                RenderGraphFrame frame = BeginFrame(graph, prepare);
                ulong frameNumber;
                try
                {
                    decals.Record(ref frame, volumes, views);
                    frameNumber = frame.FrameIdentity;
                    frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
                }
                finally { frame.Dispose(); }
                Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(frameNumber, TimeSpan.FromSeconds(10)));

                uint[] ranges = MemoryMarshal.Cast<byte, uint>(cells.Bytes).ToArray();
                uint[] references = MemoryMarshal.Cast<byte, uint>(indices.Bytes).ToArray();
                Assert.Equal(16u, ranges[5 * 2 + 1]);
                Assert.Equal(Enumerable.Range(0, 16).Select(index => (uint)index), references.Skip((int)ranges[5 * 2]).Take(16));
                Assert.Equal(16u, ranges.Where((_, index) => index % 2 == 1).Aggregate(0u, (total, count) => total + count));
                if (iteration == 0)
                {
                    firstCells = cells.Bytes;
                    firstIndices = indices.Bytes;
                }
                else
                {
                    Assert.Equal(firstCells, cells.Bytes);
                    Assert.Equal(firstIndices, indices.Bytes);
                }
            }
        }
        finally
        {
            RemoveReadbacks(graph, cellReadback, indexReadback);
            decals.Dispose();
        }
    }

    private RenderGraphFrame BeginFrame(RenderGraph.RenderGraph graph, RenderPrepareSystems prepare)
    {
        Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.FromSeconds(10)));
        using (next)
        {
            prepare.Update();
            return graph.BeginFrame(next, in _frameOptions);
        }
    }

    private static void RemoveReadbacks(RenderGraph.RenderGraph graph, GraphReadbackId cells, GraphReadbackId indices)
    {
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            edit.Remove(cells);
            edit.Remove(indices);
            edit.Commit();
        }
        finally { edit.Dispose(); }
    }

    private sealed class ReadbackBytes : IGraphReadbackReceiver
    {
        internal byte[] Bytes { get; private set; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }

    private sealed class DecalShaders(ShaderLibrary library, Shader shared, Shader decals) : IDisposable
    {
        internal Shader Shared => shared;
        internal Shader Decals => decals;

        internal static async Task<DecalShaders> CreateAsync()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
            string shaders = Path.Combine(directory!.FullName, "assets", "Shaders");
            IGlobalSession slang = Shader.CreateSlangGlobalSession();
            SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
            settings.SearchPaths = [shaders];
            var library = new ShaderLibrary(slang, settings);
            try
            {
                Shader shared = await library.CreateAsync("Shared screen binning", "screen_binning.slang", File.ReadAllText(Path.Combine(shaders, "screen_binning.slang")));
                Shader decals = await library.CreateAsync("Material decal binning", "material_decal_binning.slang", File.ReadAllText(Path.Combine(shaders, "material_decal_binning.slang")));
                return new(library, shared, decals);
            }
            catch { library.Dispose(); throw; }
        }

        public void Dispose() => library.Dispose();
    }
}
