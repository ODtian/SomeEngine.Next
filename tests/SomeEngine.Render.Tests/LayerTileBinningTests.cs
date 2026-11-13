using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Features.Layers;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class LayerTileBinningTests
{
    [Fact]
    public void GeometryStagesReceiveEveryCulledClusterInTheirTiles()
    {
        CulledCluster[] clusters = Enumerable.Range(0, 1025).Select(index =>
        {
            uint first = index % 5 == 0 ? 1u : 0u;
            uint count = first == 0 && index % 2 == 0 ? 2u : 1u;
            return new CulledCluster(first, count, index % 3 == 0 ? 7u : index % 3 == 1 ? 3u : 5u);
        }).ToArray();
        using var device = new TileBinningDevice();
        using var context = new TileBinningContext(device, clusters);

        context.Render();

        for (uint tile = 0; tile < 2; tile++)
        {
            uint[] expectedTile = clusters.Select((cluster, index) => (cluster, index))
                .Where(pair => tile >= pair.cluster.FirstTile && tile < pair.cluster.FirstTile + pair.cluster.TileCount)
                .Select(pair => (uint)pair.index).ToArray();
            Assert.True(expectedTile.Length > 256);
            for (uint stage = 0; stage < 3; stage++)
            {
                uint[] expected = expectedTile.Where(index => (clusters[index].Stages & (1u << (int)stage)) != 0).ToArray();
                uint rangeIndex = (stage * 2 + tile) * 2;
                uint offset = context.TileRanges.Values[rangeIndex];
                uint count = context.TileRanges.Values[rangeIndex + 1];
                uint[] actual = context.Clusters.Values.AsSpan((int)offset, (int)count).ToArray();
                Assert.Equal(expected.Order(), actual.Order());
            }
        }
        for (uint stage = 0; stage < 3; stage++)
        {
            uint first = context.WorkRanges.Values[stage * 2];
            Assert.Equal(2u, context.WorkRanges.Values[stage * 2 + 1]);
            Assert.Equal(new uint[] { 2, 1, 1 }, context.Dispatch.Values.AsSpan((int)stage * 3, 3).ToArray());
            for (uint tile = 0; tile < 2; tile++)
            {
                LayerStageWork work = context.Works.Values[first + tile];
                Assert.Equal(tile, work.Tile);
                Assert.Equal(stage, work.RasterStage);
                uint rangeIndex = (stage * 2 + tile) * 2;
                Assert.Equal(context.TileRanges.Values[rangeIndex], work.ClusterOffset);
                Assert.Equal(context.TileRanges.Values[rangeIndex + 1], work.ClusterCount);
            }
        }
        Assert.Equal(0u, context.Diagnostics.Values[0]);
    }

    [Fact]
    public void GrowingOverflowedListsRestoresEveryTileAndGeometryStage()
    {
        CulledCluster[] clusters = Enumerable.Range(0, 513).Select(index =>
            new CulledCluster(index % 5 == 0 ? 1u : 0u, index % 2 == 0 && index % 5 != 0 ? 2u : 1u,
                index % 3 == 0 ? 7u : index % 3 == 1 ? 3u : 5u)).ToArray();
        uint capacity = (uint)clusters.Sum(cluster => (long)cluster.TileCount);
        using var device = new TileBinningDevice();
        using var reference = new TileBinningContext(device, clusters, capacity);
        using var context = new TileBinningContext(device, clusters, 1);

        context.Render(reference);

        Assert.True(context.Works.Values.Sum(work => (long)work.ClusterCount) <
            reference.Works.Values.Sum(work => (long)work.ClusterCount));
        Assert.False(context.Diagnostics.Values.SequenceEqual(reference.Diagnostics.Values));
        ulong version = context.StructureVersion;

        context.ResizeLists(capacity);

        Assert.Equal(version + 1u, context.StructureVersion);
        context.Render(reference);
        Assert.Equal(reference.Diagnostics.Values, context.Diagnostics.Values);
        for (uint stage = 0; stage < 3; stage++)
        {
            uint first = context.WorkRanges.Values[stage * 2];
            uint count = context.WorkRanges.Values[stage * 2 + 1];
            uint referenceFirst = reference.WorkRanges.Values[stage * 2];
            uint referenceCount = reference.WorkRanges.Values[stage * 2 + 1];
            Assert.Equal(referenceCount, count);
            var expected = reference.Works.Values.AsSpan((int)referenceFirst, (int)referenceCount).ToArray()
                .Select(work => (work.Tile, work.RasterStage, work.ClusterCount)).Order();
            var actual = context.Works.Values.AsSpan((int)first, (int)count).ToArray()
                .Select(work => (work.Tile, work.RasterStage, work.ClusterCount)).Order();
            Assert.Equal(expected, actual);
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct CulledCluster(uint FirstTile, uint TileCount, uint Stages);

    private sealed class ReadbackValues<T> : IGraphReadbackReceiver where T : struct
    {
        internal T[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }

    private sealed class TileBinningDevice : IDisposable
    {
        internal BinningTestDevice Graphics { get; } = new(DeviceFeatures.IndirectCommands);
        internal ShaderLibrary Shaders { get; }
        internal RenderGraph.RenderGraph Graph { get; }
        internal IndirectCommandLayout DispatchLayout { get; }
        internal Shader SharedShader { get; }

        internal TileBinningDevice()
        {
            IGlobalSession slang = Shader.CreateSlangGlobalSession();
            SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
            string directory = TileBinningContext.ShaderDirectory();
            settings.SearchPaths = [directory];
            Shaders = new ShaderLibrary(slang, settings);
            SharedShader = Shaders.CreateAsync("Layer shared binning", "screen_binning.slang",
                File.ReadAllText(Path.Combine(directory, "screen_binning.slang"))).AsTask().GetAwaiter().GetResult();
            Graph = new RenderGraph.RenderGraph(Graphics.Backend, Graphics.Device,
                [Graphics.Backend.GetQueue(Graphics.Device, QueueType.Graphics)]);
            DispatchLayout = Graphics.Backend.CreateIndirectCommandLayout(Graphics.Device, new(
                [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)], 12, label: "Layer tile binning expansion"));
        }

        public void Dispose()
        {
            Graph.RetireAfterSubmittedFrames(DispatchLayout);
            Graph.Dispose();
            Shaders.Dispose();
            Graphics.Dispose();
        }
    }

    private sealed class TileBinningContext : IDisposable
    {
        private readonly BinningTestDevice _graphics;
        private readonly ShaderLibrary _shaders;
        private readonly RenderGraph.RenderGraph _graph;
        private readonly CulledCluster[] _clusters;
        private readonly GraphBufferInput _input;
        private readonly GraphBufferSrvId _inputView;
        private readonly Shader _sharedShader;
        private LayerTileBinningResources _resources;
        private readonly List<GraphReadbackId> _readbacks = [];
        private uint _listCapacity;
        internal ulong StructureVersion => _graph.StructureVersion;
        internal readonly ReadbackValues<uint> TileRanges = new();
        internal readonly ReadbackValues<uint> Clusters = new();
        internal readonly ReadbackValues<LayerStageWork> Works = new();
        internal readonly ReadbackValues<uint> WorkRanges = new();
        internal readonly ReadbackValues<uint> Dispatch = new();
        internal readonly ReadbackValues<uint> Diagnostics = new();

        internal TileBinningContext(TileBinningDevice device, CulledCluster[] clusters, uint? listCapacity = null)
        {
            _clusters = clusters;
            _listCapacity = listCapacity ?? (uint)clusters.Length * 2;
            _graphics = device.Graphics;
            _shaders = device.Shaders;
            _graph = device.Graph;
            _sharedShader = device.SharedShader;
            string directory = ShaderDirectory();
            Shader tiles = _shaders.CreateAsync("Layer tile binning", "layer_tile_binning.slang",
                File.ReadAllText(Path.Combine(directory, "layer_tile_binning.slang"))).AsTask().GetAwaiter().GetResult();
            Shader source = _shaders.CreateAsync("Layer culling source", "layer_tile_source.slang", ShaderSource)
                .AsTask().GetAwaiter().GetResult();
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                _input = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _inputView = edit.CreateBufferSrv(_input, structureStride: 16);
                _resources = LayerTileBinningPasses.Add(ref edit, _graphics.Pipelines, _sharedShader, tiles, source,
                    device.DispatchLayout, (uint)clusters.Length, 2, 3, _listCapacity,
                    (name, _) => name == "LayerCulledClusters"
                        ? GraphParameterResourceBinding.ReadOnlyBufferDescriptor(_inputView) : null,
                    [_inputView], Diagnostics);
                Read(ref edit, _resources.StageTileRanges, 48, TileRanges, "Read layer stage tile ranges");
                Read(ref edit, _resources.StageClusters, ulong.MaxValue, Clusters, "Read layer stage clusters");
                Read(ref edit, _resources.StageWorks, 96, Works, "Read layer raster work");
                Read(ref edit, _resources.StageWorkRanges, 24, WorkRanges, "Read layer raster work ranges");
                Read(ref edit, _resources.StageDispatch, 36, Dispatch, "Read layer raster dispatch");
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }

        private void Read(ref RenderGraphEdit edit, GraphBufferId buffer, ulong bytes,
            IGraphReadbackReceiver receiver, string label)
        {
            GraphReadbackId readback = edit.AddReadback(buffer, new(0, bytes), receiver, label);
            edit.OrderAfter(readback, _resources.CompletePass);
            _readbacks.Add(readback);
        }

        internal void Render(params TileBinningContext[] others)
        {
            ulong identity;
            RenderGraphFrame frame = _graph.BeginFrame();
            try
            {
                Record(ref frame);
                foreach (TileBinningContext context in others) context.Record(ref frame);
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[_graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, _graph.WaitForFrame(identity, TimeSpan.FromSeconds(20)));
        }

        private void Record(ref RenderGraphFrame frame)
        {
            frame.SetBufferInput(_input, frame.Upload<CulledCluster>(_clusters.AsSpan(), BufferUsages.ShaderRead));
            LayerTileBinningPasses.SetFrameParameters(ref frame, _resources, (uint)_clusters.Length,
                (uint)_clusters.Length, 2, 3, _listCapacity);
        }

        internal void ResizeLists(uint capacity)
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                LayerTileBinningResources resources = LayerTileBinningPasses.ResizeLists(ref edit, in _resources,
                    _graphics.Pipelines, _sharedShader, capacity);
                edit.Commit();
                _resources = resources;
                _listCapacity = capacity;
            }
            finally { edit.Dispose(); }
        }

        public void Dispose()
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                foreach (GraphReadbackId readback in _readbacks) edit.Remove(readback);
                LayerTileBinningPasses.Remove(ref edit, _resources);
                edit.Remove(_inputView);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }

        internal static string ShaderDirectory()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "layer_tile_binning.slang")))
                directory = directory.Parent!;
            return Path.Combine(directory.FullName, "assets", "Shaders");
        }

        private const string ShaderSource = """
            __exported import layer_tile_binning;
            struct CulledLayerCluster
            {
                uint FirstTile;
                uint TileCount;
                uint Stages;
                uint Padding;
            };
            StructuredBuffer<CulledLayerCluster> LayerCulledClusters;
            struct CullingSource : ILayerTileClusterSource
            {
                static uint GetVisibleCluster(uint itemIndex) { return itemIndex; }
                static ScreenBinningBounds GetTileBounds(uint itemIndex)
                {
                    CulledLayerCluster cluster = LayerCulledClusters[itemIndex];
                    ScreenBinningBounds bounds;
                    bounds.Minimum = uint3(cluster.FirstTile,0,0);
                    bounds.Size = uint3(cluster.TileCount,1,1);
                    return bounds;
                }
                static uint GetTileIndex(uint itemIndex,uint3 tile) { return tile.x; }
                static bool HasRasterStage(uint visibleCluster,uint stage)
                { return (LayerCulledClusters[visibleCluster].Stages & (1u<<stage))!=0; }
            };
            [shader("compute")][numthreads(256,1,1)]
            void CountScreenItems(uint3 id:SV_DispatchThreadID)
            { ScreenBinningCount<LayerTileBinningItem<CullingSource>>(ScreenBinningThreadIndex(id)); }
            [shader("compute")][numthreads(256,1,1)]
            void ExpandScreenItems(uint3 id:SV_DispatchThreadID)
            { ScreenBinningExpand<LayerTileBinningItem<CullingSource>>(ScreenBinningThreadIndex(id)); }
            [shader("compute")][numthreads(256,1,1)]
            void FillScreenItems(uint3 id:SV_DispatchThreadID)
            { ScreenBinningFillUnordered<LayerTileBinningItem<CullingSource>>(ScreenBinningThreadIndex(id)); }
            [shader("compute")][numthreads(256,1,1)]
            void LayerMarkRasterStages(uint3 id:SV_DispatchThreadID)
            { MarkLayerRasterStages<CullingSource>(ScreenBinningThreadIndex(id)); }
            """;
    }
}
