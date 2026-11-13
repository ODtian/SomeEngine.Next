using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowFilterRequestTests
{
    [Fact]
    public async Task RequestsAddedAfterCaptureStayOutsideFilterPropagationOnRepeatedFrames()
    {
        const uint pagesPerRow = 16, entriesPerView = 256, entryCount = 768;
        using var graphics = new BinningTestDevice(DeviceFeatures.IndirectCommands);
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "vsm_prefilter_request_indices.slang")))
            directory = directory.Parent!;
        string shaderDirectory = Path.Combine(directory.FullName, "assets", "Shaders");
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        settings.SearchPaths = [shaderDirectory];
        using var shaders = new ShaderLibrary(slang, settings);
        Shader filter = await shaders.CreateAsync("Filter request list", "vsm_prefilter_request_indices.slang",
            File.ReadAllText(Path.Combine(shaderDirectory, "vsm_prefilter_request_indices.slang")));
        Shader inputs = await shaders.CreateAsync("Filter request inputs", "filter_request_inputs.slang", """
            __exported import vsm_prefilter_request_common;
            StructuredBuffer<uint> InitialFilterRequests;
            [shader("compute")][numthreads(64,1,1)]
            void InitializeFilterRequests(uint3 thread : SV_DispatchThreadID)
            {
                uint requestCount, stride;
                InitialFilterRequests.GetDimensions(requestCount, stride);
                uint index = thread.x;
                if (index < FilterRequests.PageTableEntryCount)
                {
                    RequestedPageFlags[index] = index < requestCount ? VSM_PAGE_REQUEST_DETAIL : 0u;
                    RequestedPageIndices[index] = index < requestCount ? InitialFilterRequests[index] : 0u;
                }
                if (index == 0u) RequestedPageCount[0] = requestCount;
                uint width, height;
                PageReceiverMasks.GetDimensions(width, height);
                if (index < width * height) PageReceiverMasks[uint2(index % width, index / width)] = 0u;
            }
            [shader("compute")][numthreads(1,1,1)]
            void AddFilterRequestAfterCapture()
            {
                PublishVirtualShadowRequest(255u, VSM_PAGE_REQUEST_DETAIL,
                    FilterRequests.FrameIndex, FilterRequests.PageTableEntryCount);
            }
            """);
        using IndirectCommandLayout dispatchLayout = graphics.Backend.CreateIndirectCommandLayout(graphics.Device,
            new([new(IndirectArgumentType.Dispatch)], 12, label: "Filter request dispatch"));
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        var bindings = new Dictionary<string, GraphParameterResourceBinding>();
        GraphBufferInput shadowViews, shadowRanges, initialRequests;
        GraphBufferId flags;
        VirtualShadowFilterRequestResources propagation;
        var readback = new RequestReadback();
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            shadowViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
            shadowRanges = edit.CreateBufferInput(BufferUsages.ShaderRead);
            initialRequests = edit.CreateBufferInput(BufferUsages.ShaderRead);
            flags = edit.CreatePersistentBuffer(new(entryCount * 4,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource));
            GraphBufferId indices = edit.CreatePersistentBuffer(new(entryCount * 4,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
            GraphBufferId count = edit.CreatePersistentBuffer(new(4, BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
            GraphTextureId masks = edit.CreatePersistentTexture(new(TextureDimension.Texture2D,
                pagesPerRow, pagesPerRow * 2 * 3, 1, 1, 1, 1, Format.R32UInt, TextureUsages.Storage));
            GraphBufferUavId flagsView = edit.CreateBufferUav(flags, structureStride: 4);
            GraphBufferUavId indicesView = edit.CreateBufferUav(indices, structureStride: 4);
            GraphBufferUavId countView = edit.CreateBufferUav(count, structureStride: 4);
            GraphTextureUavId maskView = edit.CreateTextureUav(masks, format: Format.R32UInt,
                dimension: TextureViewDimension.Texture2D);
            bindings["ShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(
                edit.CreateBufferSrv(shadowViews, structureStride: VirtualShadowView.SizeInBytes), PipelineSync.ComputeShading);
            bindings["ShadowRanges"] = GraphParameterResourceBinding.ReadOnlyBuffer(
                edit.CreateBufferSrv(shadowRanges, structureStride: VirtualShadowRange.SizeInBytes), PipelineSync.ComputeShading);
            bindings["InitialFilterRequests"] = GraphParameterResourceBinding.ReadOnlyBuffer(
                edit.CreateBufferSrv(initialRequests, structureStride: 4), PipelineSync.ComputeShading);
            bindings["RequestedPageFlags"] = GraphParameterResourceBinding.WritableBuffer(flagsView, PipelineSync.ComputeShading);
            bindings["RequestedPageCount"] = GraphParameterResourceBinding.WritableBuffer(countView, PipelineSync.ComputeShading);
            bindings["RequestedPageIndices"] = GraphParameterResourceBinding.WritableBuffer(indicesView,
                PipelineSync.ComputeShading, GraphAccessMode.Write, resultContents: ResourceContentState.Defined,
                contentScope: BufferContentScope.CountBoundedPrefix);
            bindings["PageReceiverMasks"] = GraphParameterResourceBinding.StorageTexture(maskView, PipelineSync.ComputeShading);
            propagation = VirtualShadowFilterRequestPasses.Add(ref edit, graphics.Pipelines, filter,
                dispatchLayout, indices, count, new RequestResources(bindings));
            bindings["FilterRequests"] = GraphParameterResourceBinding.ConstantBuffer(propagation.ParametersView, PipelineSync.ComputeShading);
            var initializeBindings = new Dictionary<string, GraphParameterResourceBinding>(bindings)
            {
                ["RequestedPageFlags"] = GraphParameterResourceBinding.WritableBuffer(flagsView,
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                ["RequestedPageIndices"] = GraphParameterResourceBinding.WritableBuffer(indicesView,
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                ["RequestedPageCount"] = GraphParameterResourceBinding.WritableBuffer(countView,
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                ["PageReceiverMasks"] = GraphParameterResourceBinding.StorageTexture(maskView,
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
            };
            GraphPassId initialize = AddCompute(ref edit, graphics.Pipelines, inputs,
                "InitializeFilterRequests", new(24, 1, 1), initializeBindings);
            GraphPassId addRequest = AddCompute(ref edit, graphics.Pipelines, inputs,
                "AddFilterRequestAfterCapture", new(1, 1, 1), bindings);
            edit.OrderAfter(propagation.CapturePass, initialize);
            edit.OrderAfter(addRequest, propagation.CapturePass);
            edit.OrderAfter(propagation.CompletePass, addRequest);
            GraphReadbackId result = edit.AddReadback(flags, new(0, entryCount * 4), readback, "Read propagated filter request set");
            edit.OrderAfter(result, propagation.CompletePass);
            edit.Commit();
        }
        finally { edit.Dispose(); }

        var views = new VirtualShadowView[3];
        for (uint index = 0; index < views.Length; index++)
            views[index] = new()
            {
                VirtualResolution = pagesPerRow * 128, PageSize = 128, MipLevelCount = 1,
                PageTableOffset = index * entriesPerView, ClipmapLevel = (int)index, ClipmapLevelCount = 3,
                RangeId = index + 1u, RangeGeneration = 1u,
                Flags = VirtualShadowViewFlags.PrefilteredDistant, ReceiverMaskOriginY = index * pagesPerRow * 2,
            };
        var ranges = new VirtualShadowRange[4];
        ranges[0] = new(0, 0, 0, uint.MaxValue);
        for (uint index = 0; index < views.Length; index++)
            ranges[index + 1u] = new(index * entriesPerView, entriesPerView, 1, index);
        uint[] initial = Enumerable.Range(0, 65).Select(index => (uint)index).ToArray();
        var expected = initial.Append(255u).ToHashSet();
        foreach (uint page in initial)
            for (int level = 1; level <= 2; level++)
                expected.Add((uint)level * entriesPerView + (page / pagesPerRow >> level) * pagesPerRow + (page % pagesPerRow >> level));
        uint[] expectedPages = expected.Order().ToArray();
        uint[] firstFrame = [];
        for (int iteration = 0; iteration < 2; iteration++)
        {
            RenderGraphFrame frame = graph.BeginFrame();
            ulong identity;
            try
            {
                frame.SetBufferInput(shadowViews, frame.Upload<VirtualShadowView>(views, BufferUsages.ShaderRead));
                frame.SetBufferInput(shadowRanges, frame.Upload<VirtualShadowRange>(ranges, BufferUsages.ShaderRead));
                frame.SetBufferInput(initialRequests, frame.Upload<uint>(initial, BufferUsages.ShaderRead));
                var parameters = new VirtualShadowFilterRequestParameters(3, entryCount, 7, entriesPerView);
                VirtualShadowFilterRequestPasses.Record(ref frame, propagation, parameters);
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
            uint[] requested = readback.Flags.Select((value, index) => (value, index))
                .Where(pair => pair.value != 0).Select(pair => (uint)pair.index).ToArray();
            Assert.Equal(expectedPages, requested);
            if (iteration == 0) firstFrame = requested;
            else Assert.Equal(firstFrame, requested);
        }
    }

    private static GraphPassId AddCompute(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, DispatchArguments groups, Dictionary<string, GraphParameterResourceBinding> bindings)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
        GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), groups,
            new PassOptions(RepeatCommands: true), static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data));
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline,
                     (name, _) => bindings.TryGetValue(name, out var value) ? value : null)) pass.Bind(binding);
        return id;
    }

    private sealed class RequestResources(Dictionary<string, GraphParameterResourceBinding> bindings) : IGraphResourceProvider
    {
        public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
        {
            if (bindings.TryGetValue(name, out GraphParameterResourceBinding binding))
            {
                binder = (ref RenderGraphEdit _, ref PassDefinition pass, PipelineSync _) => pass.Bind(binding);
                return true;
            }
            binder = null!;
            return false;
        }
    }

    private sealed class RequestReadback : IGraphReadbackReceiver
    {
        internal uint[] Flags = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Flags = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }
}
