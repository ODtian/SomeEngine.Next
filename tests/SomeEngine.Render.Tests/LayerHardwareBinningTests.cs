using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Features.Layers;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class LayerHardwareBinningTests
{
    [Fact]
    public async Task LargeWindowsReachEachGeometryStageOnceAcrossMultipleGroups()
    {
        const uint itemCount = 513, stageCount = 3, threshold = 4;
        var windows = Enumerable.Range(0, (int)itemCount).Select(index =>
            new ClusterWindow(2, 1, 2u + (uint)(index % 5 + 1), 3, (uint)(index % 7 + 1))).ToArray();
        using var graphics = new BinningTestDevice();
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "screen_binning.slang"))) directory = directory.Parent!;
        string shaderDirectory = Path.Combine(directory.FullName, "assets", "Shaders");
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        settings.SearchPaths = [shaderDirectory];
        using var shaders = new ShaderLibrary(slang, settings);
        Shader shared = await shaders.CreateAsync("Hardware list scan", "screen_binning.slang",
            File.ReadAllText(Path.Combine(shaderDirectory, "screen_binning.slang")));
        Shader source = await shaders.CreateAsync("Hardware cluster windows", "hardware_cluster_windows.slang", """
            __exported import layer_hardware_binning;
            __exported import layer_composition_parameters;
            struct ClusterWindow { uint4 Window; uint Stages, Padding0, Padding1, Padding2; };
            StructuredBuffer<ClusterWindow> ClusterWindows;
            struct HardwareClusterSource : ILayerHardwareClusterSource
            {
                static uint GetVisibleCluster(uint item)
                {
                    uint count, stride; ClusterWindows.GetDimensions(count, stride);
                    return item < count ? item : 0xffffffffu;
                }
                static ScreenBinningBounds GetTileBounds(uint item)
                {
                    ScreenBinningBounds bounds;
                    uint4 window = ClusterWindows[item].Window;
                    bounds.Minimum = uint3(window.xy, 0u);
                    bounds.Size = uint3(window.zw - window.xy, 1u);
                    return bounds;
                }
                static uint GetTileIndex(uint item, uint3 tile) { return tile.y * 16u + tile.x; }
                static bool HasRasterStage(uint visible, uint stage) { return (ClusterWindows[visible].Stages & (1u << stage)) != 0u; }
                static bool IsHardwareCluster(uint item)
                {
                    uint4 window = ClusterWindows[item].Window;
                    return (window.z - window.x) * (window.w - window.y) > LayerCompositionUniforms.HardwareWindowTiles;
                }
            };
            [shader("compute")][numthreads(256,1,1)]
            void LayerCountHardwareClusters(uint3 id:SV_DispatchThreadID)
            { CountLayerHardwareClusters<HardwareClusterSource>(ScreenBinningThreadIndex(id)); }
            [shader("compute")][numthreads(256,1,1)]
            void LayerFillHardwareClusters(uint3 id:SV_DispatchThreadID)
            { FillLayerHardwareClusters<HardwareClusterSource>(ScreenBinningThreadIndex(id)); }
            """);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        GraphBufferInput input;
        GraphBufferId composition;
        LayerHardwareBinningResources hardware;
        var clusters = new UIntReadback();
        var ranges = new UIntReadback();
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            input = edit.CreateBufferInput(BufferUsages.ShaderRead);
            GraphBufferSrvId inputView = edit.CreateBufferSrv(input, structureStride: 32);
            uint alignment = graphics.Device.Capabilities.Limits.ConstantBufferAlignment;
            composition = edit.CreatePersistentBuffer(new((64u + alignment - 1u) / alignment * alignment,
                BufferUsages.Constant), MemoryType.Upload);
            GraphBufferCbvId compositionView = edit.CreateBufferCbv(composition);
            hardware = LayerHardwareBinningPasses.Add(ref edit, graphics.Pipelines, shared, source, itemCount, stageCount,
                (name, _) => name switch
                {
                    "ClusterWindows" => GraphParameterResourceBinding.ReadOnlyBuffer(inputView, PipelineSync.ComputeShading),
                    "LayerCompositionUniforms" => GraphParameterResourceBinding.ConstantBuffer(compositionView, PipelineSync.ComputeShading),
                    _ => null,
                });
            GraphReadbackId clusterRead = edit.AddReadback(hardware.Clusters, new(0, itemCount * stageCount * 4), clusters, "Read hardware geometry stage clusters");
            GraphReadbackId rangeRead = edit.AddReadback(hardware.Ranges, new(0, stageCount * 8), ranges, "Read hardware geometry stage ranges");
            edit.OrderAfter(clusterRead, hardware.CompletePass);
            edit.OrderAfter(rangeRead, hardware.CompletePass);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        RenderGraphFrame frame = graph.BeginFrame();
        ulong identity;
        try
        {
            frame.SetBufferInput(input, frame.Upload<ClusterWindow>(windows, BufferUsages.ShaderRead));
            uint[] compositionWords = new uint[16];
            compositionWords[15] = threshold;
            MemoryMarshal.AsBytes(compositionWords.AsSpan()).CopyTo(frame.GetBufferDataWriteSpan(composition));
            LayerHardwareBinningPasses.Record(ref frame, hardware, itemCount, stageCount);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        for (uint stage = 0; stage < stageCount; stage++)
        {
            uint[] expected = windows.Select((window, index) => (window, index))
                .Where(pair => (pair.window.MaximumX - pair.window.MinimumX) * (pair.window.MaximumY - pair.window.MinimumY) > threshold &&
                               (pair.window.Stages & (1u << (int)stage)) != 0u)
                .Select(pair => (uint)pair.index).ToArray();
            uint first = ranges.Values[stage * 2], count = ranges.Values[stage * 2 + 1];
            uint[] actual = clusters.Values.AsSpan((int)first, (int)count).ToArray();
            Assert.Equal(expected.Order(), actual.Order());
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private readonly record struct ClusterWindow(uint MinimumX, uint MinimumY, uint MaximumX, uint MaximumY, uint Stages);

    private sealed class UIntReadback : IGraphReadbackReceiver
    {
        internal uint[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }
}
