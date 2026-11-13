using System.Diagnostics;
using SomeEngine.ArchitectureRules;

namespace SomeEngine.ArchitectureRules.Tests;

public sealed class VsmTileRuleBehaviorTests
{
    [Fact]
    public async Task CommentsAndOrdinaryStringsPassIdentifierChecks()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/raster.slang", "// uint TargetPolicy;\nstring label = \"targetPolicy\";\n");
        sources.Write("src/SomeEngine.Render.Features/Lighting/LightData.cs", "class LightData { /* GroupsPerLight */ string Text = \"ITransparentSurfaceEvaluate SinglePoint\"; }");

        CommandResult result = await sources.RunAsync("ARCH-23,ARCH-25,ARCH-30");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Errors);
        Assert.Contains("0 violation(s)", result.Output);
    }

    [Fact]
    public async Task IdentifierViolationsReportTheirFileLineAndColumn()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/raster.slang", "// TargetPolicy\nuint TargetPolicy;\n");
        sources.Write("src/SomeEngine.Render.Features/Lighting/LightData.cs", "class LightData\n{\nint GroupsPerLight;\n}\n");

        CommandResult result = await sources.RunAsync("ARCH-23,ARCH-30");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/raster.slang(2,6): error ARCH-23:", result.Errors);
        Assert.Contains("src/SomeEngine.Render.Features/Lighting/LightData.cs(3,5): error ARCH-30:", result.Errors);
        Assert.Contains("2 violation(s)", result.Output);
    }

    [Fact]
    public async Task SharedScanAndGenericExpansionPassConsumerChecks()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/material_decal_binning.slang", """
            #include "screen_binning.slang"
            struct DecalItem : IScreenBinningItem { }
            void ScanCells(uint index) { ScreenBinningScanCells(index); }
            void ExpandDecals(uint index) { ScreenBinningExpand<DecalItem>(index); }
            uint2 ReadCell(uint index) { return ScreenBinningCells[index]; }
            """
        );
        sources.Write("assets/Shaders/layer_tile_binning.slang", """
            import screen_binning;
            uint2 ReadStageTile(uint tile) { return ScreenBinningCells[tile]; }
            """);

        CommandResult result = await sources.RunAsync("ARCH-28,ARCH-29,ARCH-35");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task SharedModuleImportsSelectTheCoverageOutput()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/vsm_caster_binning.slang", """
            __exported import screen_binning;
            void ExpandPages(uint index) { ScreenBinningExpand<CasterCoverage, InvalidationOutput>(index); }
            """);
        sources.Write("assets/Shaders/clustered_light_binning.slang", """
            string description = "__exported import screen_binning;";
            void ExpandPages(uint index) { ScreenBinningExpand<LightCoverage, LightOutput>(index); }
            """);

        CommandResult result = await sources.RunAsync("ARCH-28,ARCH-29");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/clustered_light_binning.slang(1,1): error ARCH-28:", result.Errors);
        Assert.DoesNotContain("assets/Shaders/vsm_caster_binning.slang", result.Errors);
        Assert.DoesNotContain("error ARCH-29:", result.Errors);
        Assert.Contains("1 violation(s)", result.Output);
    }

    [Fact]
    public async Task ConsumerOffsetWritesFailTheSharedScanRule()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/clustered_light_binning.slang", """
            #include "screen_binning.slang"
            void ExpandLights(uint index) { ScreenBinningExpand<LightItem>(index); }
            void WriteCell(uint index) { ScreenBinningCells[index] = uint2(index, 1u); }
            """
        );

        CommandResult result = await sources.RunAsync("ARCH-28,ARCH-29");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error ARCH-28:", result.Errors);
        Assert.DoesNotContain("error ARCH-29:", result.Errors);
    }

    [Fact]
    public async Task RectangleLoopsFailTheExpansionRule()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/clustered_light_binning.slang", """
            #include "screen_binning.slang"
            void ExpandLights(uint index) { ScreenBinningExpand<LightItem>(index); }
            void WriteRectangle(uint2 low, uint2 high)
            {
                for (uint y = low.y; y <= high.y; y++)
                    for (uint x = low.x; x <= high.x; x++)
                        WriteCell(x, y);
            }
            """
        );

        CommandResult result = await sources.RunAsync("ARCH-29");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error ARCH-29:", result.Errors);
    }

    [Fact]
    public async Task EngineShadeEntriesPassMaterialChecks()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/shade_entries.slang", """
            import render_lighting;
            struct ShadeEntry<M : IMaterial>
            {
                void evaluateSurface<TOutput>(float input, TOutput output) { M.evaluateSurface(input, output); }
            }
            """
        );

        CommandResult result = await sources.RunAsync("ARCH-24");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task MaterialImplementationsKeepLightingInEngineEntries()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/test_material.slang", """
            import render_lighting;
            struct TestMaterial<TVertex> : IMaterial { }
            #include "vsm_sampling.slang"
            """
        );

        CommandResult result = await sources.RunAsync("ARCH-24");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/test_material.slang(1,8): error ARCH-24:", result.Errors);
        Assert.Contains("assets/Shaders/test_material.slang(3,1): error ARCH-24:", result.Errors);
    }

    [Fact]
    public async Task DecalBinningUsesItsOwnGrid()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/material_decal_binning.slang", "StructuredBuffer<ClusteredLightGridParameters> LightingViews;");

        CommandResult result = await sources.RunAsync("ARCH-35");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error ARCH-35:", result.Errors);
        Assert.Contains("ClusteredLightGridParameters", result.Errors);
        Assert.Contains("LightingViews", result.Errors);
    }

    [Fact]
    public async Task SurfaceEvaluationInterpretsItsOwnPropertiesAndOutput()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/ink.slang", """
            struct InkMaterial : ISurfaceEvaluate<InkSurface, InkTriangle>
            {
                float3 Ink;
                float Roughness;
                void evaluateSurface<TOutput>(float input, TOutput output) { output.Write(Ink * Roughness); }
            }
            """);

        CommandResult result = await sources.RunAsync("ARCH-24,ARCH-36");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task EngineInterfacesAndEntriesKeepPropertiesInMaterialLibraries()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/material_interfaces.slang", "struct EngineOutput { float3 BaseColor; float Roughness; }");
        sources.Write("assets/Shaders/shade_entries.slang", "struct ShadeEntry<M : ISurfaceEvaluate<Surface, Triangle>> { float Metallic; float Occlusion; }");
        sources.Write("assets/Shaders/coating_material.slang", "struct CoatingParameters { float3 BaseColor; float Roughness; }");
        sources.Write("assets/Shaders/brdf.slang", "float EvaluateBrdf(float Roughness) { return Roughness; }");

        CommandResult result = await sources.RunAsync("ARCH-36");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/material_interfaces.slang", result.Errors);
        Assert.Contains("assets/Shaders/shade_entries.slang", result.Errors);
        Assert.DoesNotContain("assets/Shaders/coating_material.slang", result.Errors);
        Assert.DoesNotContain("assets/Shaders/brdf.slang", result.Errors);
        Assert.Contains("4 violation(s)", result.Output);
    }

    [Fact]
    public async Task MaterialPropertyNamesInCommentsAndStringsPassEngineChecks()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/engine_material_context.slang", """
            // BaseColor Roughness
            /* Metallic Occlusion */
            string description = "BaseColor Roughness Metallic Occlusion";
            struct Radiance { float3 Value; }
            """);

        CommandResult result = await sources.RunAsync("ARCH-36");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ShadowPageRequestsUseTheDefinedLibraryAndVisibilityCallers()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/vsm_page_requests.slang", """
            void MarkVirtualShadowPages() { }
            void RequestVisibleReceiver() { MarkVirtualShadowPages(); }
            """);
        sources.Write("assets/Shaders/vsm_visibility_requests.slang", """
            void RequestDepthPages() { MarkVirtualShadowPages(); }
            void evaluateTransparency() { }
            interface ITransparentSurfaceEvaluate { }
            void markShadowPages() { }
            """);
        sources.Write("assets/Shaders/layer_visibility_output.slang", """
            struct LayerVisibilityOutput { static void Write() { MarkVirtualShadowPages(); } };
            void ShadeFragments() { MarkVirtualShadowPages(); }
            """);
        sources.Write("assets/Shaders/vsm_receiver_requests.slang", "void MarkVirtualShadowPages() { }");

        CommandResult result = await sources.RunAsync("ARCH-25");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/vsm_visibility_requests.slang(2,", result.Errors);
        Assert.Contains("assets/Shaders/vsm_visibility_requests.slang(3,", result.Errors);
        Assert.Contains("assets/Shaders/vsm_visibility_requests.slang(4,", result.Errors);
        Assert.DoesNotContain("assets/Shaders/vsm_visibility_requests.slang(1,", result.Errors);
        Assert.Contains("assets/Shaders/layer_visibility_output.slang(2,", result.Errors);
        Assert.DoesNotContain("assets/Shaders/layer_visibility_output.slang(1,", result.Errors);
        Assert.Contains("assets/Shaders/vsm_receiver_requests.slang", result.Errors);
        Assert.DoesNotContain("assets/Shaders/vsm_page_requests.slang", result.Errors);
        Assert.Contains("5 violation(s)", result.Output);
    }

    [Fact]
    public async Task FragmentStorageRulesIdentifyCodeAndPreserveFixtureText()
    {
        using var sources = new SourceRepository();
        sources.Write("src/SomeEngine.Render.Features/Layers/LayerStorage.cs", """
            class LayerStorage
            {
                public uint MaximumRounds;
                const string Fixture = "CandidatesPerRound CandidateBudget IncompletePixels";
            }
            """);
        sources.Write("assets/Shaders/layer_storage.slang", """
            uint LayerDeferredPageRequests;
            string fixture = "LayerCandidateOutput LayerPrepareRound";
            // LayerSortTileLists LayerRetireClusters
            """);
        sources.Write("tests/LayerFixture.cs", "class LayerFixture { const string Source = \"MaximumRounds LayerCandidateOutput\"; }");

        CommandResult result = await sources.RunAsync("ARCH-38");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("MaximumRounds", result.Errors);
        Assert.Contains("LayerDeferredPageRequests", result.Errors);
        Assert.DoesNotContain("CandidatesPerRound", result.Errors);
        Assert.DoesNotContain("LayerCandidateOutput", result.Errors);
        Assert.DoesNotContain("LayerFixture.cs", result.Errors);
        Assert.Contains("2 violation(s)", result.Output);
    }

    [Fact]
    public async Task LayerCompositionConsumesPayloadsThroughTheSharedShadeEntry()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/ink.slang", "struct Ink : ISurfaceEvaluate<Surface, Triangle> { }");
        sources.Write("assets/Shaders/brdf.slang", "float EvaluateBrdf(float roughness) { return roughness; }");
        sources.Write("assets/Shaders/shade_surface_entries.slang", """
            struct ShadeSurface<M : IMaterial>
            {
                static void Shade<TOutput>(TOutput output) { M.evaluateSurface(output); }
            }
            """);
        sources.Write("assets/Shaders/layer_composite.slang", """
            __exported import ink;
            void Composite() { material.evaluateSurface<LayerPayloadOutput>(output); }
            """);
        sources.Write("assets/Shaders/layer_sort.slang", "import brdf;");
        sources.Write("assets/Shaders/layer_copy.slang", """
            import shade_surface_entries;
            string fixture = "import ink; material.evaluateSurface(output);";
            """);

        CommandResult result = await sources.RunAsync("ARCH-39");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/layer_composite.slang(1,", result.Errors);
        Assert.Contains("assets/Shaders/layer_composite.slang(2,", result.Errors);
        Assert.Contains("assets/Shaders/layer_sort.slang", result.Errors);
        Assert.DoesNotContain("assets/Shaders/layer_copy.slang", result.Errors);
        Assert.DoesNotContain("assets/Shaders/shade_surface_entries.slang", result.Errors);
        Assert.Contains("3 violation(s)", result.Output);
    }

    [Fact]
    public async Task RetiredCompositionFilesReportTheirPathsWithoutReadingTheirContents()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/cluster_transparency_material.slang", "");
        sources.Write("assets/Shaders/material_decal_cells.slang", "");
        sources.Write("src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Ordered.cs", "");
        sources.Write("src/SomeEngine.Render.Cluster.Pipeline/ClusterStencil.cs", "");
        sources.Write("src/SomeEngine.Render.Cluster.Pipeline/Composition/ClusterBlendState.cs", "");
        sources.Write("tests/ClusterStencil.cs", "class ClusterStencil { }");

        CommandResult result = await sources.RunAsync("ARCH-27");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cluster_transparency_material.slang(1,1): error ARCH-27:", result.Errors);
        Assert.Contains("material_decal_cells.slang(1,1): error ARCH-27:", result.Errors);
        Assert.Contains("ClusterPipelineSystem.Ordered.cs(1,1): error ARCH-27:", result.Errors);
        Assert.Contains("ClusterStencil.cs(1,1): error ARCH-27:", result.Errors);
        Assert.Contains("Composition/ClusterBlendState.cs(1,1): error ARCH-27:", result.Errors);
        Assert.DoesNotContain("tests/ClusterStencil.cs", result.Errors);
        Assert.Contains("5 violation(s)", result.Output);
    }

    [Fact]
    public async Task MaterialsImportEngineResourcesAndDeclareTheirOwnTextures()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/scene_lights.slang", "StructuredBuffer<Light> SceneLights;");
        sources.Write("assets/Shaders/paint.slang", """
            import material_lighting;
            struct Paint : ISurfaceEvaluate<Surface, Triangle> { Texture2D<float4> PaintMap; };
            StructuredBuffer<Light> SceneLights;
            Texture2DArray<float> MaterialDepth;
            string description = "StructuredBuffer<Light> LightBuffer;";
            // Texture2DArray<float> LightingCookieAtlas;
            """);

        CommandResult result = await sources.RunAsync("ARCH-24");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/paint.slang(3,", result.Errors);
        Assert.Contains("assets/Shaders/paint.slang(4,", result.Errors);
        Assert.DoesNotContain("PaintMap", result.Errors);
        Assert.Contains("2 violation(s)", result.Output);
    }

    [Fact]
    public async Task QuotedImportsKeepMaterialAndEngineResourcesSeparate()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/models/ink.slang", "struct Ink : IMaterial { }");
        sources.Write("assets/Shaders/layer_composite.slang", "import \"models/ink.slang\";");
        sources.Write("assets/Shaders/paint.slang", """
            struct Paint : IMaterial { }
            import "vsm_sampling";
            string description = "import vsm_sampling;";
            """);

        CommandResult result = await sources.RunAsync("ARCH-24,ARCH-39");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/layer_composite.slang(1,", result.Errors);
        Assert.Contains("assets/Shaders/paint.slang(2,", result.Errors);
        Assert.Contains("2 violation(s)", result.Output);
    }

    [Fact]
    public async Task ShadowFrameMethodsReuseCollectionStorageAndPersistentGraphResources()
    {
        using var sources = new SourceRepository();
        sources.Write("src/SomeEngine.Render.Features/Shadows/ShadowViews.cs", """
            using System.Collections.Generic;
            class ShadowViews
            {
                List<uint> Values = new();
                void StageFrame()
                {
                    Values = new();
                    uint[] levels = new uint[2];
                    var faces = new[] { 1u, 2u };
                    var items = new List<uint>();
                    Values.Where(value => value != 0).ToArray();
                    graph.AddCopyPass();
                    graph.CreateBuffer();
                }
                void PrepareViews() { Values.Clear(); }
                void Initialize() { Values = new(); }
            }
            """);

        CommandResult result = await sources.RunAsync("ARCH-33");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ShadowViews.cs(7,", result.Errors);
        Assert.Contains("ShadowViews.cs(8,", result.Errors);
        Assert.Contains("ShadowViews.cs(9,", result.Errors);
        Assert.Contains("ShadowViews.cs(10,", result.Errors);
        Assert.Contains("ShadowViews.cs(11,", result.Errors);
        Assert.Contains("ShadowViews.cs(12,", result.Errors);
        Assert.Contains("ShadowViews.cs(13,", result.Errors);
        Assert.Contains("8 violation(s)", result.Output);
    }

    [Fact]
    public async Task ShadowIdentityChecksReadConditionalCodeAndIgnoreDescriptions()
    {
        using var sources = new SourceRepository();
        sources.Write("src/SomeEngine.Render.Features/Shadows/ShadowViews.cs", """
            class ShadowViews
            {
            #if OLD_LAYOUT
                bool _forceStateReset;
            #else
                bool viewLayoutChanged;
            #endif
                void PrepareViews(uint geometryRevision, uint rasterRevision, int instanceCount)
                {
                    if (ReferenceEquals(_frameInstances, instances)) return;
                    string description = "_previousForceStateReset geometryRevision";
                }
            }
            """);
        sources.Write("assets/Shaders/vsm_page_state.slang", """
            uint VSM_PAGE_LOCKED;
            uint VSM_PAGE_RECEIVER_DEPTH;
            // VSM_PAGE_LOCKED
            string description = "VSM_PAGE_RECEIVER_DEPTH";
            """);

        CommandResult result = await sources.RunAsync("ARCH-31,ARCH-32");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("_forceStateReset", result.Errors);
        Assert.Contains("viewLayoutChanged", result.Errors);
        Assert.Contains("8 violation(s)", result.Output);
    }

    [Fact]
    public async Task OverflowClustersRetainBothRasterCountsAndExposeTheSpillResource()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/cluster_bin_reserve.slang", """
            RWStructuredBuffer<uint> RasterSpillClusters;
            void ReserveBins(uint count, uint capacity)
            {
                if (count > capacity)
                {
                    bin.BinSWCount = 0u;
                    bin.BinHWCount = 0;
                }
            }
            """);

        CommandResult result = await sources.RunAsync("ARCH-34");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cluster_bin_reserve.slang(6,", result.Errors);
        Assert.Contains("cluster_bin_reserve.slang(7,", result.Errors);
        Assert.Contains("2 violation(s)", result.Output);
    }

    [Fact]
    public async Task RasterOverflowStorageCanBeDeclaredByTheScatterPass()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/cluster_bin_reserve.slang", "void ReserveBins() { }");
        sources.Write("assets/Shaders/cluster_bin_scatter.slang", """
            RWStructuredBuffer<uint> RasterSpillClusters;
            void ScatterBins(uint visible) { RasterSpillClusters[visible] = visible; }
            """);

        CommandResult result = await sources.RunAsync("ARCH-34");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Errors);
        Assert.Contains("0 violation(s)", result.Output);
    }

    [Fact]
    public async Task SpillResourceNamesInDescriptionsDoNotDeclareStorage()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/cluster_bin_reserve.slang", """
            // RWStructuredBuffer<uint> RasterSpillClusters;
            string description = "RWStructuredBuffer<uint> RasterSpillClusters;";
            uint BinSWCount = 0u;
            """);

        CommandResult result = await sources.RunAsync("ARCH-34");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cluster_bin_reserve.slang(1,1): error ARCH-34:", result.Errors);
        Assert.Contains("1 violation(s)", result.Output);
    }

    [Fact]
    public async Task ScalarLoopNamesRemainOutsideTheRectangleRule()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/layer_tile_binning.slang", """
            import "screen_binning";
            void ProcessHighlights(uint highlights, uint lowlights)
            {
                for (uint outer = 0; outer < highlights; outer++)
                    for (uint inner = 0; inner < lowlights; inner++)
                        Process(outer, inner);
            }
            """);

        CommandResult result = await sources.RunAsync("ARCH-28,ARCH-29");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task DefaultCommandRunsRenderingAndTileChecksTogether()
    {
        using var sources = new SourceRepository();
        sources.Write("wiki/architecture/shader/Shader-Build.md", "# Shader build\n");
        sources.Write("src/SomeEngine.Render/Feature.cs", "class Feature { void Build() { backend.CreateBuffer(); } }");
        sources.Write("assets/Shaders/raster.slang", "uint TargetPolicy;");

        CommandResult result = await sources.RunAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error ARCH-04:", result.Errors);
        Assert.Contains("error ARCH-23:", result.Errors);
        Assert.Contains("2 violation(s)", result.Output);
    }

    [Fact]
    public async Task PathSelectionRestrictsTheCheckedFiles()
    {
        using var sources = new SourceRepository();
        sources.Write("assets/Shaders/first.slang", "uint TargetPolicy;");
        sources.Write("assets/Shaders/second.slang", "uint targetPolicy;");

        CommandResult result = await sources.RunAsync("ARCH-23", "assets/Shaders/first.slang");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("assets/Shaders/first.slang", result.Errors);
        Assert.DoesNotContain("assets/Shaders/second.slang", result.Errors);
        Assert.Contains("1 violation(s)", result.Output);
    }

    private readonly record struct CommandResult(int ExitCode, string Output, string Errors);

    private sealed class SourceRepository : IDisposable
    {
        private string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "SomeEngine-ArchitectureRules-" + Guid.NewGuid().ToString("N"));

        public void Write(string path, string source)
        {
            string target = Path.Combine(DirectoryPath, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, source);
        }

        public async Task<CommandResult> RunAsync(string? rules = null, string? paths = null)
        {
            string command = typeof(ArchitectureCommand).Assembly.Location;
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(command);
            start.ArgumentList.Add(DirectoryPath);
            if (rules is not null)
            {
                start.ArgumentList.Add("--rules");
                start.ArgumentList.Add(rules);
            }
            if (paths is not null)
            {
                start.ArgumentList.Add("--paths");
                start.ArgumentList.Add(paths);
            }
            using var process = new Process { StartInfo = start };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await output, await errors);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
