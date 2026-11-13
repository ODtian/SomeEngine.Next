using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class ScreenBinningExpansionTests
{
    [Fact]
    public async Task OverlappingBoundsWriteTheirOwnPageValuesOnce()
    {
        const uint width = 112, height = 61;
        using var graphics = new BinningTestDevice(DeviceFeatures.IndirectCommands);
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "screen_binning.slang"))) directory = directory.Parent!;
        string shadersPath = Path.Combine(directory.FullName, "assets", "Shaders");
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        settings.SearchPaths = [shadersPath];
        using var shaders = new ShaderLibrary(slang, settings);
        Shader shared = await shaders.CreateAsync("Shared screen expansion", "screen_binning.slang",
            File.ReadAllText(Path.Combine(shadersPath, "screen_binning.slang")));
        Shader consumer = await shaders.CreateAsync("Page expansion output", "page_expansion_output.slang", """
            __exported import screen_binning;
            [ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
            RWStructuredBuffer<uint4> PageValues;
            struct BoundsCoverage : IScreenBinningCoverage
            {
                static ScreenBinningBounds GetBounds(uint itemIndex)
                {
                    ScreenBinningBounds bounds;
                    bounds.Minimum = itemIndex == 0u ? uint3(0u) : uint3(32u, 16u, 0u);
                    bounds.Size = uint3(80u, 45u, 1u);
                    return bounds;
                }
                static bool Intersects(uint itemIndex, uint3 page)
                {
                    return (page.x + page.y) % 7u != 0u;
                }
            };
            struct PageValueOutput : IScreenBinningOutput
            {
                static void Write(uint itemIndex, uint3 page)
                {
                    uint index = page.y * 112u + page.x;
                    uint ignored;
                    InterlockedAdd(PageValues[index].x, 1u, ignored);
                    if (itemIndex == 0u) InterlockedExchange(PageValues[index].y, 123u, ignored);
                    else InterlockedExchange(PageValues[index].z, 456u, ignored);
                    InterlockedMax(PageValues[index].w, index + 1u, ignored);
                }
            };
            [shader("compute")][numthreads(256,1,1)]
            void InitializePageValues(uint3 thread : SV_DispatchThreadID)
            {
                uint index = ScreenBinningThreadIndex(thread);
                if (index < 112u * 61u) PageValues[index] = uint4(0u);
            }
            [shader("compute")][numthreads(256,1,1)]
            void CountScreenItems(uint3 thread : SV_DispatchThreadID)
            {
                ScreenBinningCountCoverage<BoundsCoverage>(ScreenBinningThreadIndex(thread));
            }
            [shader("compute")][numthreads(256,1,1)]
            void ExpandScreenItems(uint3 thread : SV_DispatchThreadID)
            {
                ScreenBinningExpand<BoundsCoverage, PageValueOutput>(ScreenBinningThreadIndex(thread));
            }
            """);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        IndirectCommandLayout dispatch = graphics.Backend.CreateIndirectCommandLayout(graphics.Device,
            new([new IndirectArgumentDesc(IndirectArgumentType.Dispatch)], 12, label: "Checked expansion"));
        var output = new PageReadback();
        GraphBufferId values;
        GraphBufferUavId view;
        GraphPassId initialize;
        GraphReadbackId readback;
        ScreenBinningExpansionResources expansion;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(width * height * 16u,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Expanded page values"));
            view = edit.CreateBufferUav(values, structureStride: 16);
            LinkedComputePipeline initializer = LinkedComputePipeline.Create(graphics.Pipelines, consumer,
                "InitializePageValues", "Initialize expanded page values");
            uint groups = (width * height + 255u) / 256u;
            initialize = edit.AddComputeFramePass("Initialize expanded page values", PassQueueSelection.AnyCompatible(),
                new DispatchArguments(groups, 1, 1), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data));
            PassDefinition initializePass = edit.GetPass(initialize);
            initializePass.SetPipeline(initializer.Pipeline);
            initializePass.SetParameterBlock(initializer.ParameterLayout, new byte[16]);
            foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(consumer, initializer,
                (name, _) => name == "PageValues" ? GraphParameterResourceBinding.WritableBufferDescriptor(view) : null))
                initializePass.Bind(binding);
            initializePass.Write(values, BufferRange.Whole, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            ScreenBinningExpansionResources added = ScreenBinningExpansionPasses.Add(ref edit, graphics.Pipelines,
                shared, consumer, dispatch, 2, (name, _) => name == "PageValues"
                    ? GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading) : null);
            edit.OrderAfter(added.CompletePass, initialize);
            readback = edit.AddReadback(values, BufferRange.Whole, output, "Read expanded page values");
            edit.OrderAfter(readback, added.CompletePass);
            edit.Commit();
            expansion = added;
        }
        finally { edit.Dispose(); }
        try
        {
            RenderGraphFrame frame = graph.BeginFrame();
            ulong frameIdentity;
            try
            {
                ScreenBinningExpansionPasses.SetFrameParameters(ref frame, expansion, 2, 2);
                frameIdentity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(frameIdentity, TimeSpan.FromSeconds(10)));

            Assert.Equal((int)(width * height * 4u), output.Values.Length);
            for (uint y = 0; y < height; y++)
                for (uint x = 0; x < width; x++)
                {
                    bool allocated = (x + y) % 7u != 0;
                    bool current = allocated && x < 80u && y < 45u;
                    bool previous = allocated && x >= 32u && y >= 16u;
                    uint expectedWrites = (current ? 1u : 0u) + (previous ? 1u : 0u);
                    uint index = y * width + x;
                    Assert.Equal(expectedWrites, output.Values[index * 4u]);
                    Assert.Equal(current ? 123u : 0u, output.Values[index * 4u + 1u]);
                    Assert.Equal(previous ? 456u : 0u, output.Values[index * 4u + 2u]);
                    Assert.Equal(expectedWrites != 0 ? index + 1u : 0u, output.Values[index * 4u + 3u]);
                }
        }
        finally
        {
            edit = graph.BeginEdit();
            try
            {
                edit.Remove(readback);
                ScreenBinningExpansionPasses.Remove(ref edit, expansion);
                edit.Remove(initialize);
                edit.Remove(view);
                edit.Remove(values);
                edit.Commit();
            }
            finally { edit.Dispose(); }
            graph.RetireAfterSubmittedFrames(dispatch);
        }
    }

    private sealed class PageReadback : IGraphReadbackReceiver
    {
        internal uint[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }
}
