using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowReceiverMaskRegionTests
{
    [Fact]
    public void BuildingACoarseReceiverHierarchyPreservesNeighborRegions()
    {
        using var context = new ReceiverContext("#include \"vsm_allocate_pages.slang\"\n" + MaskSource + BuildSource,
            "BuildCoarseReceiverHierarchy");
        RenderGraphFrame frame = context.Graph.BeginFrame();
        ulong identity;
        try
        {
            context.Record(ref frame);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        uint[] values = context.Result.Values;
        Assert.NotEqual(ReceiverContext.Untouched, values[1]);
        Assert.All(values.AsSpan(2, 6).ToArray(), value => Assert.Equal(ReceiverContext.Untouched, value));
        Assert.Equal(1u, values[8]);
        Assert.Equal(0u, values[9]);
    }

    [Fact]
    public void ResettingACoarseReceiverHierarchyPreservesNeighborRegions()
    {
        using var context = new ReceiverContext("#include \"vsm_receiver_mask_reset.slang\"\n" + MaskSource,
            "ResetVirtualShadowReceiverMasks");
        RenderGraphFrame frame = context.Graph.BeginFrame();
        ulong identity;
        try
        {
            context.Record(ref frame);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        uint[] values = context.Result.Values;
        Assert.Equal(0u, values[0]);
        Assert.Equal(0u, values[1]);
        Assert.All(values.AsSpan(2, 6).ToArray(), value => Assert.Equal(ReceiverContext.Untouched, value));
        Assert.Equal(0u, values[8]);
        Assert.Equal(0u, values[9]);
    }

    private sealed class ReceiverContext : IDisposable
    {
        internal const uint Untouched = 0x20000000u;
        private readonly BinningTestDevice _graphics = new();
        private readonly ShaderLibrary _shaders;
        private readonly GraphBufferInput _views, _ranges, _indices, _dispatch;
        private readonly GraphBufferId _resetParameters, _flags, _result;
        private readonly GraphTextureId _masks;
        private readonly GraphPassId[] _passes;
        private readonly GraphReadbackId _readback;
        private readonly List<GraphBufferSrvId> _srvs = [];
        private readonly List<GraphBufferUavId> _uavs = [];
        private readonly List<GraphTextureUavId> _mipViews = [];
        private readonly GraphTextureSrvId _maskRead;
        private readonly GraphBufferCbvId _resetView;
        internal global::SomeEngine.RenderGraph.RenderGraph Graph { get; }
        internal MaskReadback Result { get; } = new();

        internal ReceiverContext(string source, string entry)
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
            string directory = Path.Combine(root!.FullName, "assets", "Shaders");
            IGlobalSession global = Shader.CreateSlangGlobalSession();
            SessionDesc settings = TestShaderLoading.CreateSessionDescription(global);
            settings.SearchPaths = [directory];
            _shaders = new(global, settings);
            Shader masks = _shaders.CreateAsync("Receiver region operations", "receiver_region_checks.slang",
                source)
                .AsTask().GetAwaiter().GetResult();
            Shader filter = _shaders.CreateAsync("Receiver region filtering", "receiver_region_filter.slang", FilterSource)
                .AsTask().GetAwaiter().GetResult();
            Graph = new(_graphics.Backend, _graphics.Device, [_graphics.Backend.GetQueue(_graphics.Device, QueueType.Graphics)]);
            RenderGraphEdit edit = Graph.BeginEdit();
            try
            {
                _views = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _ranges = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _indices = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _dispatch = edit.CreateBufferInput(BufferUsages.ShaderRead);
                uint alignment = _graphics.Device.Capabilities.Limits.ConstantBufferAlignment;
                _resetParameters = edit.CreateBuffer(new(alignment, BufferUsages.Constant), MemoryType.Upload);
                _resetView = edit.CreateBufferCbv(_resetParameters);
                _flags = edit.CreatePersistentBuffer(new(85 * 4, BufferUsages.ShaderWrite));
                _result = edit.CreatePersistentBuffer(new(40, BufferUsages.ShaderWrite | BufferUsages.CopySource));
                _masks = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, 128, 128, 1, 8, 1, 1,
                    Format.R32UInt, TextureUsages.Sampled | TextureUsages.Storage));
                var resources = new Dictionary<string, GraphParameterResourceBinding>
                {
                    ["ReceiverReset"] = GraphParameterResourceBinding.ConstantBuffer(_resetView, PipelineSync.ComputeShading),
                };
                ReadInput(ref edit, resources, "ShadowViews", _views, VirtualShadowView.SizeInBytes);
                ReadInput(ref edit, resources, "ShadowRanges", _ranges, VirtualShadowRange.SizeInBytes);
                ReadInput(ref edit, resources, "RequestedPageIndices", _indices, 4);
                ReadInput(ref edit, resources, "PageRequestDispatchArgs", _dispatch, 0);
                GraphBufferUavId flagsView = edit.CreateBufferUav(_flags, structureStride: 4);
                GraphBufferUavId resultsView = edit.CreateBufferUav(_result, structureStride: 4);
                _uavs.Add(flagsView); _uavs.Add(resultsView);
                resources.Add("RequestedPageFlags", GraphParameterResourceBinding.WritableBuffer(flagsView,
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial,
                    resultContents: ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedRanges));
                resources.Add("ReceiverTestResults", GraphParameterResourceBinding.WritableBufferDescriptor(resultsView));
                for (uint mip = 0; mip < 8; mip++)
                {
                    GraphTextureUavId view = edit.CreateTextureUav(_masks, new(mip, 1, 0, 1, TextureAspects.Color),
                        Format.R32UInt, TextureViewDimension.Texture2D);
                    _mipViews.Add(view);
                    resources.Add(mip == 0 ? "PageReceiverMasks" : "PageReceiverMaskMip" + mip,
                        GraphParameterResourceBinding.StorageTexture(view, PipelineSync.ComputeShading));
                }
                _maskRead = edit.CreateTextureSrv(_masks, new(0, 8, 0, 1, TextureAspects.Color),
                    Format.R32UInt, TextureViewDimension.Texture2D);
                resources.Add("RasterTargetReceiverMasks", GraphParameterResourceBinding.SampledTexture(_maskRead, PipelineSync.ComputeShading));
                var initializeResources = new Dictionary<string, GraphParameterResourceBinding>(resources);
                for (uint mip = 0; mip < 8; mip++)
                    initializeResources[mip == 0 ? "PageReceiverMasks" : "PageReceiverMaskMip" + mip] =
                        GraphParameterResourceBinding.StorageTexture(_mipViews[(int)mip], PipelineSync.ComputeShading,
                            GraphAccessMode.Write, WriteCoverage.Complete);
                GraphPassId initialize = Add(ref edit, masks, "InitializeReceiverRegions", new(16, 16, 1), initializeResources);
                GraphPassId update = Add(ref edit, masks, entry, new(1, 1, 1), resources);
                GraphPassId read = Add(ref edit, masks, "ReadReceiverRegionMasks", new(1, 1, 1), resources, new(0, 32));
                GraphPassId regions = Add(ref edit, filter, "ReadReceiverRegionAcceptance", new(1, 1, 1), resources, new(32, 8));
                edit.OrderAfter(update, initialize);
                edit.OrderAfter(read, update);
                edit.OrderAfter(regions, read);
                _readback = edit.AddReadback(_result, BufferRange.Whole, Result, "Coarse receiver region values");
                edit.OrderAfter(_readback, regions);
                _passes = [initialize, update, read, regions];
                edit.Commit();

            }
            finally { edit.Dispose(); }
        }

        private void ReadInput(ref RenderGraphEdit edit, Dictionary<string, GraphParameterResourceBinding> resources,
            string name, GraphBufferInput input, uint stride)
        {
            GraphBufferSrvId view = edit.CreateBufferSrv(input, structureStride: stride);
            _srvs.Add(view);
            resources.Add(name, GraphParameterResourceBinding.ReadOnlyBuffer(view, PipelineSync.ComputeShading));
        }

        private GraphPassId Add(ref RenderGraphEdit edit, Shader shader, string entry, DispatchArguments dispatch,
            Dictionary<string, GraphParameterResourceBinding> bindings, BufferRange resultRange = default)
        {
            LinkedComputePipeline pipeline = LinkedComputePipeline.Create(_graphics.Pipelines, shader, entry, entry);
            GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), dispatch,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments groups) => commands.Dispatch(groups));
            PassDefinition pass = edit.GetPass(id);
            pass.SetPipeline(pipeline.Pipeline);
            pass.SetParameterBlock(pipeline.ParameterLayout);
            foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline, (name, _) =>
            {
                return bindings.TryGetValue(name, out GraphParameterResourceBinding value) ? value : null;
            })) pass.Bind(binding);
            if (resultRange.Size != 0)
                pass.Write(_result, resultRange, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            return id;
        }

        internal void Record(ref RenderGraphFrame frame)
        {
            var view = new VirtualShadowView
            {
                VirtualResolution = 1024, PageSize = 128, MipLevelCount = 4,
                RangeId = 1, RangeGeneration = 1,
                Flags = VirtualShadowViewFlags.UseReceiverMask,
            };
            frame.SetBufferInput(_views, frame.Upload(in view, BufferUsages.ShaderRead, "Receiver region view"));
            frame.SetBufferInput(_ranges, frame.Upload<VirtualShadowRange>(
                [new(0, 0, 0, uint.MaxValue), new(0, 85, 1, 0)], BufferUsages.ShaderRead, "Receiver region range"));
            frame.SetBufferInput(_indices, frame.Upload<uint>([80u], BufferUsages.ShaderRead, "Coarse mip page request"));
            frame.SetBufferInput(_dispatch, frame.Upload<uint>([0u, 0u, 0u, 1u], BufferUsages.ShaderRead, "Receiver request count"));
            uint count = 1;
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(_resetParameters), in count);
        }

        public void Dispose()
        {
            if (!Graph.IsLost)
            {
                RenderGraphEdit edit = Graph.BeginEdit();
                try
                {
                    edit.Remove(_readback);
                    foreach (GraphPassId pass in _passes) edit.Remove(pass);
                    foreach (GraphTextureUavId view in _mipViews) edit.Remove(view);
                    edit.Remove(_maskRead);
                    foreach (GraphBufferUavId view in _uavs) edit.Remove(view);
                    foreach (GraphBufferSrvId view in _srvs) edit.Remove(view);
                    edit.Remove(_resetView);
                    edit.Remove(_resetParameters); edit.Remove(_flags); edit.Remove(_result); edit.Remove(_masks);
                    edit.Commit();
                }
                finally { edit.Dispose(); }
            }
            Graph.Dispose(); _shaders.Dispose(); _graphics.Dispose();
        }
    }

    private sealed class MaskReadback : IGraphReadbackReceiver
    {
        internal uint[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    private const string MaskSource = """
        RWStructuredBuffer<uint> ReceiverTestResults;
        [shader("compute")] [numthreads(8,8,1)]
        void InitializeReceiverRegions(uint3 id : SV_DispatchThreadID)
        {
            if (all(id.xy < 128u)) PageReceiverMasks[id.xy] = id.x == 4u && (id.y == 16u || id.y == 17u) ? 0xffffffffu : 0u;
            if (all(id.xy < 64u)) PageReceiverMaskMip1[id.xy] = all(id.xy == uint2(2,8)) ? 0x20000000u : 0u;
            if (all(id.xy < 32u)) PageReceiverMaskMip2[id.xy] = 0x20000000u;
            if (all(id.xy < 16u)) PageReceiverMaskMip3[id.xy] = 0x20000000u;
            if (all(id.xy < 8u)) PageReceiverMaskMip4[id.xy] = 0x20000000u;
            if (all(id.xy < 4u)) PageReceiverMaskMip5[id.xy] = 0x20000000u;
            if (all(id.xy < 2u)) PageReceiverMaskMip6[id.xy] = 0x20000000u;
            if (all(id.xy < 1u)) PageReceiverMaskMip7[id.xy] = 0x20000000u;
        }
        [shader("compute")] [numthreads(1,1,1)]
        void ReadReceiverRegionMasks()
        {
            uint2 address = uint2(4,16);
            ReceiverTestResults[0] = PageReceiverMasks[address];
            ReceiverTestResults[1] = PageReceiverMaskMip1[address >> 1u];
            ReceiverTestResults[2] = PageReceiverMaskMip2[address >> 2u];
            ReceiverTestResults[3] = PageReceiverMaskMip3[address >> 3u];
            ReceiverTestResults[4] = PageReceiverMaskMip4[address >> 4u];
            ReceiverTestResults[5] = PageReceiverMaskMip5[address >> 5u];
            ReceiverTestResults[6] = PageReceiverMaskMip6[address >> 6u];
            ReceiverTestResults[7] = PageReceiverMaskMip7[address >> 7u];
        }
        """;
    private const string BuildSource = """
        [shader("compute")] [numthreads(1,1,1)]
        void BuildCoarseReceiverHierarchy() { BuildReceiverMaskHierarchy(ShadowViews[0], 2u, uint2(0)); }
        """;
    private const string FilterSource = """
        #define CLUSTER_TARGET_BOUNDS_ONLY 1
        #include "cluster_raster_target_filter.slang"
        RWStructuredBuffer<uint> ReceiverTestResults;
        [shader("compute")] [numthreads(1,1,1)]
        void ReadReceiverRegionAcceptance()
        {
            ClusterRasterTargetView target = {};
            target.PagesPerRow = 2u;
            target.PageSize = 128u;
            target.ReceiverMaskEnabled = 1u;
            target.ReceiverMaskOrigin = (4u << 16u) | 16u;
            ReceiverTestResults[8] = ClusterTargetReceiverHierarchyAccepts(target, uint2(0), uint2(255)) ? 1u : 0u;
            target.PagesPerRow = 1u;
            target.ReceiverMaskOrigin = (6u << 16u) | 16u;
            ReceiverTestResults[9] = ClusterTargetReceiverHierarchyAccepts(target, uint2(0), uint2(127)) ? 1u : 0u;
        }
        """;
}
