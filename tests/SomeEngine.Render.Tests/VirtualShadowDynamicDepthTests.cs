using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowDynamicDepthTests
{
    [Theory]
    [InlineData(true, 1u, 0u)]
    [InlineData(false, 0u, 16u)]
    public async Task DirectionalDynamicDepthUpdatesFollowTheReceiverRefreshSetting(
        bool receiverRefresh, uint expectedRefreshedPages, uint expectedCasterFlags)
    {
        var settings = new VirtualShadowMapSettings { DirectionalDynamicReceiverRefresh = receiverRefresh };
        var view = new VirtualShadowView
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 128, PageSize = 128,
            AtlasPagesPerRow = 1, MipLevelCount = 1, CacheGeneration = 7,
            RangeId = 1, RangeGeneration = 1,
            LightKind = VirtualShadowLightKind.Directional,
            Flags = settings.ApplyDirectionalDynamicReceiverRefresh(VirtualShadowViewFlags.UseReceiverMask,
                VirtualShadowLightKind.Directional),
        };
        using var context = await DynamicDepthContext.CreateAsync(view);
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
        Assert.Equal(expectedRefreshedPages, context.RefreshedPages.Value);
        Assert.Equal(expectedCasterFlags, context.CasterPage.Value.Flags);
    }

    private sealed class DynamicDepthContext : IDisposable
    {
        private readonly BinningTestDevice _graphics = new(DeviceFeatures.IndirectCommands);
        private readonly ShaderLibrary _shaders;
        private readonly IndirectCommandLayout _dispatch;
        private readonly Dictionary<GraphBufferInput, byte[]> _inputs = [];
        private readonly Dictionary<string, GraphParameterResourceBinding> _bindings = [];
        private readonly Dictionary<string, GraphBufferUavId> _bufferViews = [];
        private readonly GraphTextureUavId[] _maskViews = new GraphTextureUavId[8];
        private readonly GraphBufferId _allocation, _invalidation;
        private readonly ScreenBinningExpansionResources _expansion;
        internal RenderGraph.RenderGraph Graph { get; }
        internal CountReadback RefreshedPages { get; } = new();
        internal MetadataReadback CasterPage { get; } = new();

        internal static async Task<DynamicDepthContext> CreateAsync(VirtualShadowView view)
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "vsm_allocate_pages.slang")))
                directory = directory.Parent!;
            string shadersPath = Path.Combine(directory.FullName, "assets", "Shaders");
            IGlobalSession slang = Shader.CreateSlangGlobalSession();
            SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
            settings.SearchPaths = [shadersPath];
            var shaders = new ShaderLibrary(slang, settings);
            Shader allocation = await shaders.CreateAsync("Dynamic receiver page updates", "dynamic_receiver_page_checks.slang",
                "#include \"vsm_allocate_pages.slang\"\n" + InitializeSource);
            Shader caster = await shaders.CreateAsync("Dynamic caster page updates", "vsm_caster_binning.slang",
                File.ReadAllText(Path.Combine(shadersPath, "vsm_caster_binning.slang")));
            Shader shared = await shaders.CreateAsync("Dynamic caster expansion", "screen_binning.slang",
                File.ReadAllText(Path.Combine(shadersPath, "screen_binning.slang")));
            return new(shaders, allocation, caster, shared, view);
        }

        private DynamicDepthContext(ShaderLibrary shaders, Shader allocationShader, Shader casterShader,
            Shader sharedShader, VirtualShadowView view)
        {
            _shaders = shaders;
            Graph = new(_graphics.Backend, _graphics.Device, [_graphics.Backend.GetQueue(_graphics.Device, QueueType.Graphics)]);
            _dispatch = _graphics.Backend.CreateIndirectCommandLayout(_graphics.Device,
                new([new IndirectArgumentDesc(IndirectArgumentType.Dispatch)], 12));
            RenderGraphEdit edit = Graph.BeginEdit();
            try
            {
                Input(ref edit, "ShadowViews", new[] { view }, VirtualShadowView.SizeInBytes);
                Input(ref edit, "ShadowRanges", new VirtualShadowRange[]
                {
                    new(0, 0, 0, uint.MaxValue), new(0, 1, 1, 0),
                }, VirtualShadowRange.SizeInBytes);
                Input(ref edit, "RequestedPageIndices", new uint[] { 0 }, 4);
                Input(ref edit, "RequestedPageCount", new uint[] { 1 }, 4);
                Input(ref edit, "RequestedPageFlags", new uint[] { 1 }, 4);
                Input(ref edit, "InstanceWorldBounds", new Vector4[]
                {
                    new(-.2f,-.2f,.1f,0), new(.2f,.2f,.4f,0),
                    new(-.3f,-.3f,.1f,0), new(.1f,.1f,.4f,0),
                }, 16);
                Input(ref edit, "ChangedInstances", new uint[] { 0 }, 4);
                Input(ref edit, "RasterTargetDepthCacheClasses", new uint[] { 0 }, 4);
                Input(ref edit, "PreviousDepthCacheClasses", new uint[] { 0 }, 4);
                Input(ref edit, "PreviousCasterRows", new uint[] { 0 }, 4);
                Input(ref edit, "LodWorldBounds", new Vector4[] { Vector4.Zero }, 16);
                Input(ref edit, "AllocatedPageHierarchy", new uint[] { 1 }, 4);
                Input(ref edit, "AllocatedPageRectBounds", new UIntRectangle[] { new(0,0,0,0) }, 16);
                _allocation = Uniforms(ref edit, "PageAllocation");
                _invalidation = Uniforms(ref edit, "Invalidation");
                Buffer(ref edit, "ShadowPageTable", 1, 4);
                Buffer(ref edit, "PageAssignments", 1, 4);
                GraphBufferId metadata = Buffer(ref edit, "PageMetadata", 1, VirtualShadowPageMetadata.SizeInBytes);
                Buffer(ref edit, "ShadowPageLists", 8, 4);
                Buffer(ref edit, "ActivePhysicalPages", 1, 16);
                GraphBufferId activeCount = Buffer(ref edit, "ActivePhysicalPageCount", 1, 4);
                Buffer(ref edit, "UncachedPageRectBounds", 1, 16);
                Buffer(ref edit, "RasterTargetPageHierarchy", 1, 8);
                Buffer(ref edit, "ShadowAllocationFailures", 3, 4);
                GraphTextureId masks = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, 128, 128, 1, 8, 1, 1,
                    Format.R32UInt, TextureUsages.Sampled | TextureUsages.Storage));
                for (uint mip = 0; mip < 8; mip++)
                {
                    _maskViews[mip] = edit.CreateTextureUav(masks, new(mip, 1, 0, 1, TextureAspects.Color),
                        Format.R32UInt, TextureViewDimension.Texture2D);
                    _bindings.Add(MaskName(mip), GraphParameterResourceBinding.StorageTexture(_maskViews[mip], PipelineSync.ComputeShading));
                }
                var initialBindings = new Dictionary<string, GraphParameterResourceBinding>(_bindings);
                foreach ((string name, GraphBufferUavId bufferView) in _bufferViews)
                    initialBindings[name] = GraphParameterResourceBinding.WritableBuffer(bufferView, PipelineSync.ComputeShading,
                        GraphAccessMode.Write, WriteCoverage.Complete);
                for (uint mip = 0; mip < 8; mip++)
                    initialBindings[MaskName(mip)] = GraphParameterResourceBinding.StorageTexture(_maskViews[mip],
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
                GraphPassId initialize = Add(ref edit, allocationShader, "InitializeCachedDynamicPage", new(64, 1, 1), initialBindings);
                GraphPassId allocate = Add(ref edit, allocationShader, "AllocateVirtualShadowPages", new(1, 1, 1), _bindings);
                edit.OrderAfter(allocate, initialize);
                GraphReadbackId refreshed = edit.AddReadback(activeCount, BufferRange.Whole, RefreshedPages,
                    "Directional receiver dynamic page updates");
                edit.OrderAfter(refreshed, allocate);
                GraphPassId initializeCaster = Add(ref edit, allocationShader, "InitializeCachedDynamicPage", new(64, 1, 1), initialBindings);
                edit.OrderAfter(initializeCaster, refreshed);
                _expansion = ScreenBinningExpansionPasses.Add(ref edit, _graphics.Pipelines, sharedShader, casterShader,
                    _dispatch, 2, (name, _) => _bindings.TryGetValue(name, out var binding) ? binding : null);
                edit.OrderAfter(_expansion.CompletePass, initializeCaster);
                GraphReadbackId invalidated = edit.AddReadback(metadata, BufferRange.Whole, CasterPage,
                    "Changed dynamic caster page flags");
                edit.OrderAfter(invalidated, _expansion.CompletePass);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }

        private void Input<T>(ref RenderGraphEdit edit, string name, T[] values, uint stride) where T : unmanaged
        {
            GraphBufferInput input = edit.CreateBufferInput(BufferUsages.ShaderRead);
            _inputs.Add(input, MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
            GraphBufferSrvId view = edit.CreateBufferSrv(input, structureStride: stride);
            _bindings.Add(name, GraphParameterResourceBinding.ReadOnlyBuffer(view, PipelineSync.ComputeShading));
        }

        private GraphBufferId Buffer(ref RenderGraphEdit edit, string name, uint count, uint stride)
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new((ulong)count * stride,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, name));
            GraphBufferUavId view = edit.CreateBufferUav(buffer, structureStride: stride);
            _bufferViews.Add(name, view);
            _bindings.Add(name, GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading));
            return buffer;
        }

        private GraphBufferId Uniforms(ref RenderGraphEdit edit, string name)
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new(_graphics.Device.Capabilities.Limits.ConstantBufferAlignment,
                BufferUsages.Constant, name), MemoryType.Upload);
            _bindings.Add(name, GraphParameterResourceBinding.ConstantBuffer(edit.CreateBufferCbv(buffer), PipelineSync.ComputeShading));
            return buffer;
        }

        private GraphPassId Add(ref RenderGraphEdit edit, Shader shader, string entry, DispatchArguments dispatch,
            Dictionary<string, GraphParameterResourceBinding> resources)
        {
            LinkedComputePipeline pipeline = LinkedComputePipeline.Create(_graphics.Pipelines, shader, entry, entry);
            GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), dispatch,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments groups) => commands.Dispatch(groups));
            PassDefinition pass = edit.GetPass(id);
            pass.SetPipeline(pipeline.Pipeline);
            pass.SetParameterBlock(pipeline.ParameterLayout);
            foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline,
                (name, _) => resources.TryGetValue(name, out var binding) ? binding : null)) pass.Bind(binding);
            return id;
        }

        internal void Record(ref RenderGraphFrame frame)
        {
            foreach ((GraphBufferInput input, byte[] bytes) in _inputs)
                frame.SetBufferInput(input, frame.Upload<byte>(bytes, BufferUsages.ShaderRead));
            var allocation = new VirtualShadowPageAllocationUniforms { PageTableEntryCount = 1, PageTableEntriesPerView = 1,
                MaxPages = 1, ViewCount = 1, FrameIndex = 2, DeferredInvalidationBudget = -1, HierarchyWordsPerView = 1, MipLevelCount = 1 };
            var invalidation = new VirtualShadowInvalidationUniforms { ChangedInstanceCount = 1, ViewCount = 1,
                PageTableEntryCount = 1, HierarchyWordsPerView = 1, MipLevelCount = 1 };
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(_allocation), in allocation);
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(_invalidation), in invalidation);
            ScreenBinningExpansionPasses.SetFrameParameters(ref frame, _expansion, 2, 2);
        }

        public void Dispose()
        {
            Graph.RetireAfterSubmittedFrames(_dispatch);
            Graph.Dispose();
            _shaders.Dispose();
            _graphics.Dispose();
        }

        private static string MaskName(uint mip) => mip == 0 ? "PageReceiverMasks" : "PageReceiverMaskMip" + mip;
    }

    private readonly record struct UIntRectangle(uint X, uint Y, uint Z, uint W);
    private sealed class CountReadback : IGraphReadbackReceiver
    {
        internal uint Value;
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Value = MemoryMarshal.Read<uint>(bytes);
    }
    private sealed class MetadataReadback : IGraphReadbackReceiver
    {
        internal VirtualShadowPageMetadata Value;
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Value = MemoryMarshal.Read<VirtualShadowPageMetadata>(bytes);
    }

    private const string InitializeSource = """
        [shader("compute")][numthreads(256,1,1)]
        void InitializeCachedDynamicPage(uint3 id : SV_DispatchThreadID)
        {
            if (id.x == 0u)
            {
                ShadowPageTable[0] = PackVirtualShadowPage(1u, 1u) | VSM_PAGE_ANY_DEPTH;
                PageAssignments[0] = 0u;
                VirtualShadowPageMetadata metadata = {};
                metadata.StaticDepthGeneration = 7u;
                metadata.RequestFlags = VSM_PAGE_REQUEST_DETAIL;
                metadata.Generation = 7u;
                metadata.RangeId = 1u;
                metadata.RangeGeneration = 1u;
                PageMetadata[0] = metadata;
                ActivePhysicalPages[0] = uint4(0u);
                ActivePhysicalPageCount[0] = 0u;
                UncachedPageRectBounds[0] = uint4(0xffffffffu,0xffffffffu,0u,0u);
                RasterTargetPageHierarchy[0] = uint2(0u);
            }
            if (id.x < 3u) ShadowAllocationFailures[id.x] = 0u;
            if (id.x < 8u) ShadowPageLists[id.x] = 0u;
            if (id.x < 128u*128u) PageReceiverMasks[uint2(id.x%128u,id.x/128u)] = 0xffffffffu;
            if (id.x < 64u*64u) PageReceiverMaskMip1[uint2(id.x%64u,id.x/64u)] = 0u;
            if (id.x < 32u*32u) PageReceiverMaskMip2[uint2(id.x%32u,id.x/32u)] = 0u;
            if (id.x < 16u*16u) PageReceiverMaskMip3[uint2(id.x%16u,id.x/16u)] = 0u;
            if (id.x < 8u*8u) PageReceiverMaskMip4[uint2(id.x%8u,id.x/8u)] = 0u;
            if (id.x < 4u*4u) PageReceiverMaskMip5[uint2(id.x%4u,id.x/4u)] = 0u;
            if (id.x < 2u*2u) PageReceiverMaskMip6[uint2(id.x%2u,id.x/2u)] = 0u;
            if (id.x == 0u) PageReceiverMaskMip7[uint2(0u)] = 0u;
        }
        """;
}
