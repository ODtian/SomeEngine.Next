using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowCasterExpansionTests
{
    [Fact]
    public async Task ExpandedCasterChangesFollowCurrentHistoricalAndUnboundedPageCoverage()
    {
        const uint entriesPerView = 84, entryCount = entriesPerView * 2;
        VirtualShadowView[] shadowViews =
        [
            new() { LightViewProjection = Matrix4x4.Identity, VirtualResolution = 1024, PageSize = 128,
                MipLevelCount = 3, AtlasPagesPerRow = 16, CacheGeneration = 7, RangeId = 1, RangeGeneration = 1,
                VirtualPageOriginX = 3, VirtualPageOriginY = -2 },
            new() { LightViewProjection = Matrix4x4.Identity, VirtualResolution = 1024, PageSize = 128,
                MipLevelCount = 3, AtlasPagesPerRow = 16, CacheGeneration = 9, PageTableOffset = entriesPerView,
                RangeId = 2, RangeGeneration = 1,
                Flags = VirtualShadowViewFlags.ForceCacheDynamicCoarse, VirtualPageOriginX = -3, VirtualPageOriginY = 2 },
        ];
        Vector4[] bounds =
        [
            new(-.85f, -.65f, .1f, 0), new(-.45f, -.25f, .4f, 0),
            new(.2f, .25f, .1f, 0), new(.6f, .65f, .4f, 0),
            new(.4f, -.7f, .1f, 0), new(.8f, -.3f, .4f, 0),
            new(-.65f, .2f, .1f, 1), new(-.25f, .6f, .4f, 1),
            new(1, 1, 1, 0), new(-1, -1, -1, 0), new(1, 1, 1, 0), new(-1, -1, -1, 0),
        ];
        Vector4[] lodBounds = [new(-.9f, .5f, .1f, 2), new(.2f, .9f, .4f, 0)];
        Vector3 previousTranslation = new(.1f, -.1f, 0);
        var pageTable = new uint[entryCount];
        var metadata = new VirtualShadowPageMetadata[entryCount];
        var requested = new uint[entryCount];
        var allocated = new UIntRectangle[6];
        var expectedEntryChanges = new bool[entryCount];
        var expectedPageChanges = new bool[entryCount];
        var expectedMetadata = new VirtualShadowPageMetadata[entryCount];
        for (uint viewIndex = 0; viewIndex < shadowViews.Length; viewIndex++)
        {
            VirtualShadowView view = shadowViews[viewIndex];
            uint mipOffset = 0;
            for (uint mip = 0; mip < 3; mip++)
            {
                uint dimension = 8u >> (int)mip;
                allocated[viewIndex * 3 + mip] = new(1, 0, dimension - 1, dimension - 1);
                for (uint y = 0; y < dimension; y++)
                    for (uint x = 0; x < dimension; x++)
                    {
                        int worldX = (view.VirtualPageOriginX >> (int)mip) + (int)x;
                        int worldY = (view.VirtualPageOriginY >> (int)mip) + (int)y;
                        uint tableIndex = view.PageTableOffset + mipOffset +
                            ((uint)worldY & (dimension - 1)) * dimension + ((uint)worldX & (dimension - 1));
                        pageTable[tableIndex] = VirtualShadowPageEntry.Encode(tableIndex, 16);
                        requested[tableIndex] = (x & 1u) == 0 ? 1u : 0u;
                        metadata[tableIndex] = new() { StaticDepthGeneration = 100, RequestFlags = 2,
                            Generation = view.CacheGeneration, RangeId = view.RangeId, RangeGeneration = view.RangeGeneration,
                            WorldPageX = worldX, WorldPageY = worldY };
                        VirtualShadowPageMetadata expected = metadata[tableIndex];
                        if (x >= 1)
                        {
                            ApplyReference(bounds[0], bounds[1], 1, false);
                            ApplyReference(bounds[6], bounds[7], 0, false, true);
                            ApplyReference(bounds[4], bounds[5], 0, false);
                            ApplyReference(bounds[2] + new Vector4(previousTranslation, 0),
                                bounds[3] + new Vector4(previousTranslation, 0), 1, false);
                            ApplyReference(lodBounds[0], lodBounds[1], 1, true);
                        }
                        expectedMetadata[tableIndex] = expected;

                        void ApplyReference(Vector4 minimum, Vector4 maximum, uint depthClass, bool deferred, bool unbounded = false)
                        {
                            uint lowX = (uint)((minimum.X * .5f + .5f) * dimension);
                            uint lowY = (uint)((maximum.Y * -.5f + .5f) * dimension);
                            uint highX = (uint)((maximum.X * .5f + .5f) * dimension);
                            uint highY = (uint)((minimum.Y * -.5f + .5f) * dimension);
                            lowX = lowX > 0 ? lowX - 1 : 0;
                            lowY = lowY > 0 ? lowY - 1 : 0;
                            highX = Math.Min(highX + 1, dimension - 1);
                            highY = Math.Min(highY + 1, dimension - 1);
                            if (unbounded) { lowX = lowY = 0; highX = highY = dimension - 1; }
                            if (x < lowX || x > highX || y < lowY || y > highY) return;
                            if (deferred) { expectedPageChanges[tableIndex] = true; return; }
                            if (depthClass == 0 && viewIndex == 1 && requested[tableIndex] == 0) return;
                            expectedPageChanges[tableIndex] = true;
                            if (depthClass == 1) expected.StaticDepthGeneration = 0;
                            expectedEntryChanges[tableIndex] = true;
                        }
                    }
                mipOffset += dimension * dimension;
            }
        }
        PageReadback result = await InvalidatePagesAsync(new(shadowViews,
            [new(0, 0, 0, 0), new(0, entriesPerView, 1, 0), new(entriesPerView, entriesPerView, 1, 1)],
            bounds, [0, 1, 0x20000000u, 0x80000000u], [5], [1], [1, uint.MaxValue, uint.MaxValue],
            lodBounds, requested, Enumerable.Repeat(uint.MaxValue, 50).ToArray(), allocated, pageTable, metadata,
            new VirtualShadowInvalidationUniforms { ChangedInstanceCount = 4, ViewCount = 2,
                PageTableEntryCount = entryCount, PageDilation = 1, HierarchyWordsPerView = 25,
                MipLevelCount = 3, DeferredChangedInstanceCount = 1, PreviousWorldTranslation = previousTranslation }));
        for (int page = 0; page < entryCount; page++)
        {
            Assert.Equal(expectedEntryChanges[page], result.Table[page] != pageTable[page]);
            Assert.Equal(expectedPageChanges[page], result.Metadata[page].Flags != 0);
            VirtualShadowPageMetadata actual = result.Metadata[page];
            actual.Flags = 0;
            Assert.Equal(expectedMetadata[page], actual);
        }
    }

    [Theory]
    [InlineData(-.5f, -.5f, 2f, .5f, .5f, 3f, false)]
    [InlineData(10f, -.5f, -3f, 11f, .5f, -2f, false)]
    [InlineData(-.5f, -.5f, -30f, .5f, .5f, -20f, false)]
    [InlineData(-.1f, -.1f, -.5f, .1f, .1f, -.2f, false)]
    [InlineData(-.5f, -.5f, -3f, .5f, .5f, -2f, true)]
    [InlineData(-.5f, -.5f, -2f, .5f, .5f, 2f, true)]
    public async Task OnlyCastersIntersectingTheLightViewInvalidateCachedPages(
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ, bool intersects)
    {
        Vector4 minimum = new(minX, minY, minZ, 0), maximum = new(maxX, maxY, maxZ, 0);
        CasterPageData pages = CreateCasterPages(new()
        {
            LightViewProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, 10),
            LightKind = VirtualShadowLightKind.Spot,
        }) with { Bounds = [minimum, maximum, minimum, maximum] };
        PageReadback result = await InvalidatePagesAsync(pages);
        Assert.Equal(intersects, result.Metadata.Any(page => page.StaticDepthGeneration == 0));
        Assert.Equal(pages.Metadata.Select(page => page.WorldPageX), result.Metadata.Select(page => page.WorldPageX));
        Assert.Equal(pages.Metadata.Select(page => page.WorldPageY), result.Metadata.Select(page => page.WorldPageY));
    }

    [Fact]
    public async Task LeavingTheLightStillInvalidatesPagesCoveredByPreviousBounds()
    {
        CasterPageData pages = CreateCasterPages(new()
        {
            LightViewProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, 10),
            LightKind = VirtualShadowLightKind.Spot,
        }) with { Bounds = [new(-.5f, -.5f, 2, 0), new(.5f, .5f, 3, 0), new(-.5f, -.5f, -3, 0), new(.5f, .5f, -2, 0)] };
        PageReadback result = await InvalidatePagesAsync(pages);
        Assert.Contains(result.Metadata, page => page.StaticDepthGeneration == 0);
    }

    [Fact]
    public async Task CasterChangesPreservePagesOutsideTheAllocatedRectangle()
    {
        CasterPageData pages = CreateCasterPages(new() { LightViewProjection = Matrix4x4.Identity }) with
        {
            Allocated = [new(uint.MaxValue, uint.MaxValue, 0, 0)],
            AllocatedHierarchy = new uint[7],
        };
        PageReadback result = await InvalidatePagesAsync(pages);
        Assert.Equal(pages.PageTable, result.Table);
        Assert.Equal(pages.Metadata, result.Metadata);
    }

    [Theory]
    [InlineData(2.2f, false)]
    [InlineData(1.5f, true)]
    public async Task LocalLightRangePreservesCachedPagesOutsideItsInfluence(float nearestAxis, bool intersects)
    {
        Vector4 minimum = new(nearestAxis, -4, -2.9f, 0), maximum = new(2.9f, 4, -nearestAxis, 0);
        CasterPageData pages = CreateCasterPages(new()
        {
            LightViewProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, 3),
            LightKind = VirtualShadowLightKind.Point, LightRange = 3,
        }) with { Bounds = [minimum, maximum, minimum, maximum] };
        PageReadback result = await InvalidatePagesAsync(pages);
        Assert.Equal(intersects, result.Metadata.Any(page => page.StaticDepthGeneration == 0));
    }

    [Fact]
    public async Task LeavingLocalLightRangeStillInvalidatesPreviousCoverage()
    {
        CasterPageData pages = CreateCasterPages(new()
        {
            LightViewProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, 3),
            LightKind = VirtualShadowLightKind.Point, LightRange = 3,
        }) with { Bounds = [new(2.2f, -4, -2.9f, 0), new(2.9f, 4, -2.2f, 0), new(1.5f, -4, -2.9f, 0), new(2.9f, 4, -1.5f, 0)] };
        PageReadback result = await InvalidatePagesAsync(pages);
        Assert.Contains(result.Metadata, page => page.StaticDepthGeneration == 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectionalReceiverRefreshSelectsWhetherDynamicCasterChangesInvalidatePages(bool refresh)
    {
        VirtualShadowMapSettings settings = new() { DirectionalDynamicReceiverRefresh = refresh };
        CasterPageData pages = CreateCasterPages(new()
        {
            LightViewProjection = Matrix4x4.Identity,
            Flags = settings.ApplyDirectionalDynamicReceiverRefresh(VirtualShadowViewFlags.UseReceiverMask, VirtualShadowLightKind.Directional),
        }) with { StaticBits = [0], PreviousStaticBits = [0] };
        PageReadback result = await InvalidatePagesAsync(pages);
        Assert.Equal(refresh, result.Metadata.All(page => page.Flags == 0));
    }

    private static CasterPageData CreateCasterPages(VirtualShadowView view)
    {
        view.VirtualResolution = 512;
        view.PageSize = 128;
        view.AtlasPagesPerRow = 4;
        view.MipLevelCount = 1;
        view.CacheGeneration = 7;
        view.RangeId = view.RangeGeneration = 1;
        var entries = new uint[16];
        var metadata = new VirtualShadowPageMetadata[16];
        for (uint page = 0; page < 16; page++)
        {
            entries[page] = VirtualShadowPageEntry.Encode(page, 4);
            metadata[page] = new()
            {
                StaticDepthGeneration = 100, Generation = 7, RangeId = 1, RangeGeneration = 1,
                WorldPageX = (int)(page % 4), WorldPageY = (int)(page / 4),
            };
        }
        return new([view], [new(0, 0, 0, 0), new(0, 16, 1, 0)],
            [new(-.5f, -.5f, .1f, 0), new(.5f, .5f, .4f, 0), new(-.5f, -.5f, .1f, 0), new(.5f, .5f, .4f, 0)],
            [0], [1], [1], [0], [Vector4.Zero], new uint[16], Enumerable.Repeat(uint.MaxValue, 7).ToArray(),
            [new(0, 0, 3, 3)], entries, metadata,
            new VirtualShadowInvalidationUniforms { ChangedInstanceCount = 1, ViewCount = 1,
                PageTableEntryCount = 16, HierarchyWordsPerView = 7, MipLevelCount = 1 });
    }

    private static async Task<PageReadback> InvalidatePagesAsync(CasterPageData data)
    {
        uint entryCount = (uint)data.PageTable.Length;
        uint itemCount = checked(data.Invalidation.ChangedInstanceCount * data.Invalidation.ViewCount * data.Invalidation.MipLevelCount * 2u);
        using var graphics = new BinningTestDevice(DeviceFeatures.IndirectCommands);
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "vsm_caster_binning.slang")))
            directory = directory.Parent!;
        string shadersPath = Path.Combine(directory.FullName, "assets", "Shaders");
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        settings.SearchPaths = [shadersPath];
        using var shaders = new ShaderLibrary(slang, settings);
        Shader shared = await shaders.CreateAsync("Caster shared expansion", "screen_binning.slang",
            File.ReadAllText(Path.Combine(shadersPath, "screen_binning.slang")));
        Shader expanded = await shaders.CreateAsync("Expanded caster invalidation", "vsm_caster_binning.slang",
            File.ReadAllText(Path.Combine(shadersPath, "vsm_caster_binning.slang")));
        Shader initialize = await shaders.CreateAsync("Initial caster pages", "initial_caster_pages.slang", """
            __exported import vsm_page_entry;
            StructuredBuffer<uint> InitialPageTable;
            StructuredBuffer<VirtualShadowPageMetadata> InitialPageMetadata;
            RWStructuredBuffer<uint> ShadowPageTable;
            RWStructuredBuffer<VirtualShadowPageMetadata> PageMetadata;
            [shader("compute")][numthreads(64,1,1)]
            void InitializeCasterPages(uint3 thread : SV_DispatchThreadID)
            {
                uint count, stride; InitialPageTable.GetDimensions(count, stride);
                if (thread.x >= count) return;
                ShadowPageTable[thread.x] = InitialPageTable[thread.x];
                PageMetadata[thread.x] = InitialPageMetadata[thread.x];
            }
            """);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        IndirectCommandLayout dispatch = graphics.Backend.CreateIndirectCommandLayout(graphics.Device,
            new([new IndirectArgumentDesc(IndirectArgumentType.Dispatch)], 12, label: "Caster page expansion"));
        var inputs = new Dictionary<string, GraphBufferInput>();
        var resources = new Dictionary<string, GraphParameterResourceBinding>();
        GraphBufferId uniforms;
        GraphReadbackId parallelMetadataReadback;
        GraphRegionId expansionRegion;
        GraphPassId initializeParallel;
        Dictionary<string, GraphParameterResourceBinding> parallelResources;
        var expandedReadback = new PageReadback();
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            AddInput(ref edit, inputs, resources, "ShadowViews", VirtualShadowView.SizeInBytes);
            AddInput(ref edit, inputs, resources, "ShadowRanges", 16);
            AddInput(ref edit, inputs, resources, "InstanceWorldBounds", 16);
            AddInput(ref edit, inputs, resources, "ChangedInstances", 4);
            AddInput(ref edit, inputs, resources, "RasterTargetDepthCacheClasses", 4);
            AddInput(ref edit, inputs, resources, "PreviousDepthCacheClasses", 4);
            AddInput(ref edit, inputs, resources, "PreviousCasterRows", 4);
            AddInput(ref edit, inputs, resources, "LodWorldBounds", 16);
            AddInput(ref edit, inputs, resources, "RequestedPageFlags", 4);
            AddInput(ref edit, inputs, resources, "AllocatedPageHierarchy", 4);
            AddInput(ref edit, inputs, resources, "AllocatedPageRectBounds", 16);
            AddInput(ref edit, inputs, resources, "InitialPageTable", 4);
            AddInput(ref edit, inputs, resources, "InitialPageMetadata", VirtualShadowPageMetadata.SizeInBytes);
            uniforms = edit.CreatePersistentBuffer(new(graphics.Device.Capabilities.Limits.ConstantBufferAlignment,
                BufferUsages.Constant), MemoryType.Upload);
            GraphBufferCbvId uniformsView = edit.CreateBufferCbv(uniforms);
            resources["Invalidation"] = GraphParameterResourceBinding.ConstantBuffer(uniformsView, PipelineSync.ComputeShading);
            PageBuffers parallel = CreatePages(ref edit, entryCount);
            parallelResources = PageBindings(resources, parallel);
            initializeParallel = AddCompute(ref edit, graphics.Pipelines, initialize, "InitializeCasterPages",
                new((entryCount + 63u) / 64u, 1, 1), PageBindings(resources, parallel, GraphAccessMode.Write, WriteCoverage.Complete));
            expansionRegion = edit.AddFrameRegion("Caster expansion work");
            parallelMetadataReadback = AddReadbacks(ref edit, parallel, expandedReadback, initializeParallel);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        try
        {
            RenderGraphFrame frame = graph.BeginFrame();
            ulong frameIdentity;
            try
            {
                frame.SetBufferInput(inputs["ShadowViews"], frame.Upload<VirtualShadowView>(data.Views, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["ShadowRanges"], frame.Upload<UIntRectangle>(
                    data.Ranges, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["InstanceWorldBounds"], frame.Upload<Vector4>(data.Bounds, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["ChangedInstances"], frame.Upload<uint>(data.Changes, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["RasterTargetDepthCacheClasses"], frame.Upload<uint>(data.StaticBits, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["PreviousDepthCacheClasses"], frame.Upload<uint>(data.PreviousStaticBits, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["PreviousCasterRows"], frame.Upload<uint>(data.PreviousRows, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["LodWorldBounds"], frame.Upload<Vector4>(data.LodBounds, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["RequestedPageFlags"], frame.Upload<uint>(data.Requested, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["AllocatedPageHierarchy"], frame.Upload<uint>(data.AllocatedHierarchy, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["AllocatedPageRectBounds"], frame.Upload<UIntRectangle>(data.Allocated, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["InitialPageTable"], frame.Upload<uint>(data.PageTable, BufferUsages.ShaderRead));
                frame.SetBufferInput(inputs["InitialPageMetadata"], frame.Upload<VirtualShadowPageMetadata>(data.Metadata, BufferUsages.ShaderRead));
                VirtualShadowInvalidationUniforms parameters = data.Invalidation;
                MemoryMarshal.Write(frame.GetBufferDataWriteSpan(uniforms), in parameters);
                ScreenBinningExpansionResources expansion = ScreenBinningExpansionPasses.Add(ref frame, graphics.Pipelines,
                    shared, expanded, dispatch, itemCount,
                    (name, _) => parallelResources.TryGetValue(name, out var binding) ? binding : null, expansionRegion);
                frame.OrderAfter(expansion.FirstPass, initializeParallel);
                frame.SetReadbackTag(parallelMetadataReadback, 1);
                frameIdentity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(frameIdentity, TimeSpan.FromSeconds(10)));
            return expandedReadback;
        }
        finally { graph.RetireAfterSubmittedFrames(dispatch); }
    }

    private static void AddInput(ref RenderGraphEdit edit, Dictionary<string, GraphBufferInput> inputs,
        Dictionary<string, GraphParameterResourceBinding> resources, string name, uint stride)
    {
        GraphBufferInput input = edit.CreateBufferInput(BufferUsages.ShaderRead);
        inputs.Add(name, input);
        resources.Add(name, GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(input, structureStride: stride),
            PipelineSync.ComputeShading));
    }

    private static PageBuffers CreatePages(ref RenderGraphEdit edit, uint count)
    {
        GraphBufferId table = edit.CreatePersistentBuffer(new(count * 4u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource));
        GraphBufferId metadata = edit.CreatePersistentBuffer(new(count * VirtualShadowPageMetadata.SizeInBytes,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource));
        return new(table, metadata, edit.CreateBufferUav(table, structureStride: 4),
            edit.CreateBufferUav(metadata, structureStride: VirtualShadowPageMetadata.SizeInBytes));
    }

    private static Dictionary<string, GraphParameterResourceBinding> PageBindings(
        Dictionary<string, GraphParameterResourceBinding> inputs, PageBuffers pages,
        GraphAccessMode access = GraphAccessMode.ReadWrite, WriteCoverage coverage = WriteCoverage.Partial) => new(inputs)
    {
        ["ShadowPageTable"] = GraphParameterResourceBinding.WritableBuffer(pages.TableView, PipelineSync.ComputeShading, access, coverage),
        ["PageMetadata"] = GraphParameterResourceBinding.WritableBuffer(pages.MetadataView, PipelineSync.ComputeShading, access, coverage),
    };

    private static GraphPassId AddCompute(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, DispatchArguments dispatch, Dictionary<string, GraphParameterResourceBinding> resources)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
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

    private static GraphReadbackId AddReadbacks(ref RenderGraphEdit edit, PageBuffers pages, PageReadback result, GraphPassId previous)
    {
        GraphReadbackId table = edit.AddReadback(pages.Table, BufferRange.Whole, result, "Invalidated caster page entries");
        GraphReadbackId metadata = edit.AddReadback(pages.Metadata, BufferRange.Whole, result, "Invalidated caster page metadata");
        edit.OrderAfter(table, previous);
        edit.OrderAfter(metadata, previous);
        return metadata;
    }

    private readonly record struct PageBuffers(GraphBufferId Table, GraphBufferId Metadata,
        GraphBufferUavId TableView, GraphBufferUavId MetadataView);
    private readonly record struct UIntRectangle(uint X, uint Y, uint Z, uint W);

    private sealed record CasterPageData(VirtualShadowView[] Views, UIntRectangle[] Ranges,
        Vector4[] Bounds, uint[] Changes, uint[] StaticBits, uint[] PreviousStaticBits, uint[] PreviousRows,
        Vector4[] LodBounds, uint[] Requested, uint[] AllocatedHierarchy, UIntRectangle[] Allocated,
        uint[] PageTable, VirtualShadowPageMetadata[] Metadata, VirtualShadowInvalidationUniforms Invalidation);

    private sealed class PageReadback : IGraphReadbackReceiver
    {
        internal uint[] Table = [];
        internal VirtualShadowPageMetadata[] Metadata = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            if (tag == 0) Table = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
            else Metadata = MemoryMarshal.Cast<byte, VirtualShadowPageMetadata>(bytes).ToArray();
        }
    }
}
