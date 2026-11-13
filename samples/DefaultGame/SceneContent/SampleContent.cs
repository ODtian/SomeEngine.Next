using System.Text.Json;
using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Importers;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Serialization.Containers;

namespace DefaultGame.Content;

/// <summary>Asset definitions for the sphere scene and its rendering settings.</summary>
public static class SampleContent
{
    /// <summary>Writes the default Cluster configuration against the project's existing shader assets.</summary>
    public static Guid BuildClusterShaders(string projectRoot, string engineRoot) =>
        BuildClusterShaders(new AssetProject(Path.GetFullPath(projectRoot), []), engineRoot);

    /// <summary>Writes the sample's shadow configuration against its existing shared shaders.</summary>
    public static Guid BuildShadowShaders(string projectRoot, string engineRoot) =>
        BuildShadowShaders(new AssetProject(Path.GetFullPath(projectRoot), []), engineRoot);

    /// <summary>Writes the current sample renderer configuration for an explicitly imported scene.</summary>
    public static void BuildApplicationSettings(AssetProject project, string engineRoot, AssetId<WorldAsset> scene)
    {
        EngineShaderContent.SetContentDirectory(project, engineRoot);
        Guid pipeline = BuildClusterShaders(project, engineRoot);
        Guid shadows = BuildShadowShaders(project, engineRoot);
        BuildApplicationSettings(project, engineRoot, scene.Value, pipeline, shadows);
    }

    /// <summary>Refreshes the sample renderer settings while keeping the existing scene and dimensions.</summary>
    public static async Task RefreshApplicationSettingsAsync(AssetProject project, string engineRoot,
        CancellationToken cancellationToken = default)
    {
        engineRoot = Path.GetFullPath(engineRoot);
        EngineShaderContent.SetContentDirectory(project, engineRoot);
        const string outputPath = "assets/Runtime/default.runtime.asset";
        string path = Path.Combine(project.RootPath, outputPath);
        using ApplicationSettingsAsset asset = await ApplicationSettingsAsset.OpenAsync(path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ApplicationSettingsBlock settings = asset.GetBlock<ApplicationSettingsBlock>();
        settings.Pipeline = new(BuildClusterShaders(project, engineRoot));
        settings.VirtualShadowMap = new(BuildShadowShaders(project, engineRoot));
        SetApplicationShaders(project, engineRoot, settings);
        project.RegisterAsset(await asset.SaveAsync(path, cancellationToken).ConfigureAwait(false));
        Console.WriteLine($"  Application: {outputPath}");
    }

    /// <summary>Rebuilds one project shader with the selected engine shader library.</summary>
    public static async Task RefreshShaderAsync(string projectRoot, string shaderSource, string engineRoot,
        CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        string shaders = Path.Combine(projectRoot, "assets", "Shaders");
        WriteInstanceLayout(shaders);
        await EngineShaderContent.BuildShaderAsync(projectRoot, shaderSource, engineRoot,
            [SlangShaderSharp.SlangCompileTarget.Dxil], cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Shader: {Path.GetFileName(shaderSource)}");
    }

    public static async Task BuildAsync(string projectRoot, string engineRoot, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        engineRoot = Path.GetFullPath(engineRoot);
        string shaders = Path.Combine(projectRoot, "assets", "Shaders");
        string engineShaders = Path.Combine(engineRoot, "assets", "Shaders");
        WriteInstanceLayout(shaders);
        var project = new AssetProject(projectRoot, [StandardGltfSceneImporter.CreateSourceImporter()],
            shaderIncludeDirectories: [engineShaders], shaderTargets: [SlangShaderSharp.SlangCompileTarget.Dxil]);
        WriteInstanceLayout(engineShaders);
        await EngineShaderContent.BuildAsync(engineRoot, project.ShaderTargets, cancellationToken).ConfigureAwait(false);
        EngineShaderContent.SetContentDirectory(project, engineRoot);
        string[] shaderSources = Directory.EnumerateFiles(shaders, "*.shader").Order(StringComparer.Ordinal)
            .Where(source => Path.GetFileNameWithoutExtension(source) is not ("standard" or "unlit")
                && !File.Exists(Path.Combine(engineShaders, Path.GetFileName(source)))).ToArray();
        await project.BuildAllAsync(shaderSources, 8, cancellationToken).ConfigureAwait(false);
        await SampleMaterials.BuildAsync(project, engineRoot, cancellationToken).ConfigureAwait(false);
        await SampleBlendContent.BuildAsync(project, engineRoot, cancellationToken).ConfigureAwait(false);
        Guid pipeline = BuildClusterShaders(project, engineRoot);
        Guid shadows = BuildShadowShaders(project, engineRoot);
        IReadOnlyList<Guid> imported = await project.ImportAsync("assets/Models/ico_sphere.glb", cancellationToken).ConfigureAwait(false);
        Guid mesh = imported.Single(id => project.Manifest.Assets[id].AssetType == MeshAsset.FileType);
        Guid scene = await BuildSceneAsync(project, mesh, cancellationToken).ConfigureAwait(false);
        BuildApplicationSettings(project, engineRoot, scene, pipeline, shadows);
        AssetDiagnostic[] errors = project.Validate().Where(static item => item.Severity == AssetDiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Select(static error => error.Message)));
        Console.WriteLine($"Built {project.Manifest.Assets.Count} project assets from project sources and the engine shader library.");
    }
    private static void WriteInstanceLayout(string shaders)
    {
        RenderInstancePropertyLayout layout = RenderInstancePropertyLayout.Compose(
            ClusterRenderFeature.InstanceLayout, SkinInstanceProducer.Layout);
        int skinBinding = layout.Resolve<RenderSkinBinding>(SkinInstanceProducer.BindingKey)
            .Descriptor.MetadataWordOffset;
        string source = ClusterShaderSources.BuildInstanceLayout(layout).Replace("#endif",
            $"static const uint SKIN_BINDING_METADATA_WORD = {skinBinding}u;\n\n#endif", StringComparison.Ordinal);
        WriteGeneratedFile(Path.Combine(shaders, "generated", "render_instance_layout.slang"), source);
    }

    private static void WriteGeneratedFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string normalizedContent = content.Replace("\r\n", "\n");
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), normalizedContent, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, normalizedContent);
    }

    private static Guid BuildClusterShaders(AssetProject project, string engineRoot)
    {
        const string outputPath = "assets/Pipelines/default_cluster.clusterrender.asset";
        using var settings = new ClusterShadersBlock
        {
            Operations =
            [
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialDecalBinning,
                    ResolveRequired(project, engineRoot, "assets/Shaders/material_decal_binning.shader"),
                    "CountScreenItems"),
                ComputeOperation(
                    ClusterShaderOperationRole.BvhTraversal,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_bvh_traverse.shader"),
                    "CullClusterWork"),
                ComputeOperation(
                    ClusterShaderOperationRole.LeafFlagFixups,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_leaf_flags.shader"),
                    "ApplyClusterLeafFlags"),
                ComputeOperation(
                    ClusterShaderOperationRole.PageRequests,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_page_request_complete.shader"),
                    "CompleteClusterPageRequests"),
                ComputeOperation(
                    ClusterShaderOperationRole.BvhTraversalInitialize,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_bvh_traverse_seed.shader"),
                    "SeedHierarchyRoots"),
                ComputeOperation(
                    ClusterShaderOperationRole.SceneBounds,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_scene_bounds.shader"),
                    "BuildClusterSceneBounds"),
                ComputeOperation(
                    ClusterShaderOperationRole.BvhDeferredResume,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_deferred_resume.shader"),
                    "ResumeDeferredClusterWork"),
                ComputeOperation(
                    ClusterShaderOperationRole.CullReset,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_cull_reset.shader"),
                    "ClearCull"),
                ComputeOperation(
                    ClusterShaderOperationRole.CullPhaseOne,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_cull_phase1.shader"),
                    "CullPhaseOne"),
                ComputeOperation(
                    ClusterShaderOperationRole.CullPhaseTwo,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_cull_phase2.shader"),
                    "CullPhaseTwo"),
                ComputeOperation(
                    ClusterShaderOperationRole.RasterBinningReset,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_bin_reset.shader"),
                    "ResetBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.RasterBinningCount,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_bin_count.shader"),
                    "CountBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.RasterBinningReserve,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_bin_reserve.shader"),
                    "ReserveBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.RasterBinningScatter,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_bin_scatter.shader"),
                    "ScatterBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.TessellationBinningReset,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_tessellation_bin.shader"),
                    "ResetTessellationBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.TessellationBinningCount,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_tessellation_bin.shader"),
                    "CountTessellationBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.TessellationBinningReserve,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_tessellation_bin.shader"),
                    "ReserveTessellationBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.TessellationBinningScatter,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_tessellation_bin.shader"),
                    "ScatterTessellationBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.SoftwareVisibilityRaster,
                    ResolveRequired(project, engineRoot, "assets/Shaders/sw_raster.shader"),
                    "CSSWRasterBufferAtomic64"),
                RasterOperation(
                    ClusterShaderOperationRole.HardwareVisibilityRaster,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_draw.shader"),
                    "VSVisBuffer",
                    "PSVisBufferHardwareOnly"),
                ComputeOperation(
                    ClusterShaderOperationRole.VisibilityClearImage,
                    ResolveRequired(project, engineRoot, "assets/Shaders/visibility_clear_image.shader"),
                    "ClearVisibilityWinner"),
                ComputeOperation(
                    ClusterShaderOperationRole.VisibilityClearBuffer,
                    ResolveRequired(project, engineRoot, "assets/Shaders/visibility_clear_buffer.shader"),
                    "ClearVisibilityWinner"),
                RasterOperation(
                    ClusterShaderOperationRole.VisibilityResolveImage,
                    ResolveRequired(project, engineRoot, "assets/Shaders/visibility_resolve_image.shader"),
                    "VSFullscreen",
                    "PSResolveVisibilityWinner"),
                RasterOperation(
                    ClusterShaderOperationRole.VisibilityResolveBuffer,
                    ResolveRequired(project, engineRoot, "assets/Shaders/visibility_resolve_buffer.shader"),
                    "VSFullscreen",
                    "PSResolveVisibilityWinner"),
                ComputeOperation(
                    ClusterShaderOperationRole.HiZInitialize,
                    ResolveRequired(project, engineRoot, "assets/Shaders/hiz_init.shader"),
                    "InitHiZ"),
                ComputeOperation(
                    ClusterShaderOperationRole.HiZReduce,
                    ResolveRequired(project, engineRoot, "assets/Shaders/hiz_reduce.shader"),
                    "ReduceHiZ"),
                ComputeOperation(
                    ClusterShaderOperationRole.HiZReducePair,
                    ResolveRequired(project, engineRoot, "assets/Shaders/hiz_reduce_pair.shader"),
                    "ReduceHiZPair"),
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialBinningReset,
                    ResolveRequired(project, engineRoot, "assets/Shaders/shade_bin_reset.shader"),
                    "ResetShadeBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialBinningCount,
                    ResolveRequired(project, engineRoot, "assets/Shaders/shade_bin_count.shader"),
                    "CountShadeBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialBinningReserve,
                    ResolveRequired(project, engineRoot, "assets/Shaders/shade_bin_reserve.shader"),
                    "ReserveShadeBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialBinningScatter,
                    ResolveRequired(project, engineRoot, "assets/Shaders/shade_bin_scatter.shader"),
                    "ScatterShadeBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.VisibilityResolve,
                    ResolveRequired(project, engineRoot, "assets/Shaders/cluster_resolve.shader"),
                    "CSResolve"),
            ],
        };

        using var asset = new ClusterShadersAsset(SampleAssetIds.ClusterShaders) { Name = "Default Cluster" };
        asset.SetBlock(settings);
        Guid guid = project.RegisterAsset(asset.SaveAsync(global::System.IO.Path.Combine(project.RootPath, outputPath)).AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"  ClusterPipeline: {outputPath}");
        return guid;
    }

    private static async Task<Guid> BuildSceneAsync(AssetProject project, Guid meshGuid, CancellationToken cancellationToken)
    {
        const string outputPath = "assets/Scenes/default.scene.asset";
        const int instanceCount = 1024;
        const int columns = 32;
        const float spacing = 1.5f;
        float origin = -((columns - 1) * spacing) * 0.5f;
        using var world = new World(initialEntityCapacity: 2048);
        for (int index = 0; index < instanceCount; index++)
        {
            var entity = world.CreateEntity(new WorldTransform
            {
                Qvvs = new(new Vector3(origin + index % columns * spacing, origin + index / columns * spacing, 65), Quaternion.Identity)
                { Stretch = new(0.6f) },
            });
            world.Add(entity, new MeshSource { Mesh = new(meshGuid), BoundsExpansion = 0.3f });
            world.Add(entity, new MeshMotion
            {
                Seed = unchecked((uint)index * 0x9E3779B9u),
                Amplitude = new(0.25f + (index & 3) * 0.035f,
                    0.12f + ((index >> 2) & 3) * 0.025f,
                    0.32f + ((index >> 4) & 3) * 0.04f),
            });
        }
        world.CreateEntity(new SceneCamera
        {
            Position = new(0, 0, -3), Target = new(0, 0, 65), Up = Vector3.UnitY,
            VerticalFieldOfView = MathF.PI / 4, NearPlane = 0.1f, FarPlane = 1000,
        });
        world.CreateEntity(new DirectionalLight(new(0.35f, -0.85f, 0.4f), new(1, 0.96f, 0.9f), 3));
        world.CreateEntity(new PointLight(new(0, 6, 52), 28, new(0.55f, 0.75f, 1), 10));
        world.CreateEntity(new PointLight(new(-18, 4, 58), 22, new(1, 0.55f, 0.45f), 7));
        world.CreateEntity(new PointLight(new(18, 8, 60), 24, new(0.45f, 1, 0.65f), 8));
        using var asset = new WorldAsset(SampleAssetIds.Scene) { Name = "Default" };
        asset.WriteWorld(world, SceneSerialization.CreateRegistry());
        AssetEntry entry = await asset.SaveAsync(Path.Combine(project.RootPath, outputPath), cancellationToken).ConfigureAwait(false);
        Guid id = project.RegisterAsset(entry);
        Console.WriteLine($"  Scene: {outputPath}");
        return id;
    }

    private static void BuildApplicationSettings(AssetProject project, string engineRoot, Guid sceneGuid, Guid clusterPipelineGuid, Guid virtualShadowMapGuid)
    {
        const string outputPath = "assets/Runtime/default.runtime.asset";
        using var settings = new ApplicationSettingsBlock
        {
            Scene = new(sceneGuid), Pipeline = new(clusterPipelineGuid), VirtualShadowMap = new(virtualShadowMapGuid),
            Width = 1280, Height = 720,
        };
        SetApplicationShaders(project, engineRoot, settings);
        using var asset = new ApplicationSettingsAsset(SampleAssetIds.ApplicationSettings) { Name = "Default Game" };
        asset.SetBlock(settings);
        project.RegisterAsset(asset.SaveAsync(Path.Combine(project.RootPath, outputPath)).AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"  Application: {outputPath}");
    }

    private static void SetApplicationShaders(AssetProject project, string engineRoot, ApplicationSettingsBlock settings)
    {
        settings.UiShader = new(ResolveRequired(project, engineRoot, "assets/Shaders/imgui.shader"));
        settings.UiVertexEntry = "VSMain";
        settings.UiPixelEntry = "PSMain";
        settings.PresentationShader = new(ResolveRequired(project, engineRoot, "assets/Shaders/post_tonemap.shader"));
        settings.PresentationVertexEntry = "VSMain";
        settings.PresentationPixelEntry = "PSMain";
        settings.LightingShader = new(ResolveRequired(project, engineRoot, "assets/Shaders/clustered_light_binning.shader"));
        settings.TemporalShader = new(ResolveRequired(project, engineRoot, "assets/Shaders/temporal_resolve.shader"));
        settings.TemporalVertexEntry = "VSMain";
        settings.TemporalPixelEntry = "PSMain";
        settings.TranslucencyShader = new(ResolveRequired(project, engineRoot, "assets/Shaders/translucency_composite.shader"));
        settings.TranslucencyVertexEntry = "VSMain";
        settings.TranslucencyPixelEntry = "PSMain";
        settings.DebugShader = new(ResolveRequired(project, engineRoot, "assets/Shaders/render_debug_overlay.shader"));
        settings.DebugVertexEntry = "VSMain";
        settings.DebugPixelEntry = "PSMain";
    }

    private static ShaderRef ShaderReference(Guid shaderGuid, string entryPoint)
        => new()
        {
            Asset = new(shaderGuid),
            EntryPoint = entryPoint,
        };

    private static Guid BuildShadowShaders(AssetProject project, string engineRoot)
    {
        const string outputPath = "assets/Pipelines/default_virtual_shadow.virtualshadow.asset";
        using var settings = new VirtualShadowMapShadersBlock
            {
                BlueNoise = new AssetId<TextureAsset>(SampleAssetIds.ShadowBlueNoise),
                ResetReceiverMasks = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_receiver_mask_reset.shader"),
                    "ResetVirtualShadowReceiverMasks"),
                ResetFrame = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_frame_reset.shader"),
                    "ResetVirtualShadowFrame"),
                OpaqueRequests = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_visibility_request_entries.shader"),
                    "RequestOpaqueShadowPages"),
                MarkCoarsePages = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_mark_coarse_pages.shader"),
                    "MarkVirtualShadowCoarsePages"),
                AllocatePages = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_allocate_pages.shader"),
                    "AllocateVirtualShadowPages"),
                ResidentPages = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_resident_propagation.shader"),
                    "PropagateVirtualShadowResidentPages"),
                Prefilter = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_prefilter.shader"),
                    "PrefilterVirtualShadowDistant"),
                PrefilterReset = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_prefilter_work.shader"),
                    "ResetVirtualShadowFilterWork"),
                PrefilterSelect = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_prefilter_work.shader"),
                    "SelectVirtualShadowFilterPages"),
                PrefilterRequests = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_prefilter_requests.shader"),
                    "PropagateVirtualShadowFilterRequests"),
                MergeStatic = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_merge_static.shader"),
                    "MergeVirtualShadowStaticDepth"),
                ClearPages = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_clear_pages.shader"),
                    "ClearVirtualShadowPages"),
                InitializeState = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_state_init.shader"),
                    "InitializeVirtualShadowState"),
                InvalidateCasters = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_caster_binning.shader"),
                    "CountScreenItems"),
                BuildPagePool = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_page_pool.shader"),
                    "BuildVirtualShadowPagePool"),
                CompactViews = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_view_compact.shader"),
                    "CompactVirtualShadowViews"),
                InitializePageHiZ = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_page_hiz.shader"),
                    "InitializeVirtualShadowPageHiZ"),
                ReducePageHiZ = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_page_hiz_reduce.shader"),
                    "ReduceVirtualShadowPageHiZ"),
                CompletePageDepth = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_page_depth_complete.shader"),
                    "CompleteVirtualShadowPageDepth"),
                WorkFeedback = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_work_feedback.shader"),
                    "UpdateVirtualShadowWorkThrottle"),
                DynamicResolution = ShaderReference(
                    ResolveRequired(project, engineRoot, "assets/Shaders/vsm_dynamic_resolution.shader"),
                    "UpdateVirtualShadowDynamicResolution"),
            };
        using var asset = new VirtualShadowMapShadersAsset(SampleAssetIds.ShadowShaders) { Name = "Default Virtual Shadow" };
        asset.SetBlock(settings);
        Guid guid = project.RegisterAsset(asset.SaveAsync(Path.Combine(project.RootPath, outputPath)).AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"  VirtualShadowMap: {outputPath}");
        return guid;
    }

    private static ClusterShaderOperation ComputeOperation(
        ClusterShaderOperationRole role,
        Guid shaderGuid,
        string entryPoint)
        => new()
        {
            Role = role,
            Shaders = [ShaderReference(shaderGuid, entryPoint)],
        };

    private static ClusterShaderOperation RasterOperation(
        ClusterShaderOperationRole role,
        Guid shaderGuid,
        string vertexEntryPoint,
        string pixelEntryPoint)
        => new()
        {
            Role = role,
            Shaders =
            [
                ShaderReference(shaderGuid, vertexEntryPoint),
                ShaderReference(shaderGuid, pixelEntryPoint),
            ],
        };

    private static Guid ResolveRequired(AssetProject project, string engineRoot, string sourcePath)
    {
        string definition = Path.Combine(engineRoot, "assets", "Shaders", Path.GetFileName(sourcePath));
        return File.Exists(definition) ? EngineShaderContent.GetShaderId(engineRoot, sourcePath)
            : project.Resolve(sourcePath) ?? throw new InvalidOperationException($"Required shader '{sourcePath}' has no definition.");
    }
}
