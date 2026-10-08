using System.Numerics;
using Game.Assets;
using Game.Importing;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Instances;
using SomeEngine.Serialization.Containers;
using System.Text.Json;

namespace Game.Build;

/// <summary>Project source definitions for the default scene, shader settings and application.</summary>
public static class DefaultContent
{
    public static async Task BuildAsync(string projectRoot, string engineRoot, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        engineRoot = Path.GetFullPath(engineRoot);
        string shaders = Path.Combine(projectRoot, "assets", "Shaders");
        string engineShaders = Path.Combine(engineRoot, "assets", "Shaders");
        if (!File.Exists(Path.Combine(projectRoot, "Game.Build", "Game.Build.csproj")) || !Directory.Exists(engineShaders))
            throw new DirectoryNotFoundException("Select the copied project and its engine shader library explicitly.");
        MaterialTypes.Register();
        WriteGeneratedFile(Path.Combine(shaders, "generated", "render_instance_layout.slang"),
            BuildRenderInstanceLayoutSource(ClusterRenderFeature.InstanceLayout));
        var project = new AssetProject(projectRoot, [StandardGltfSceneImporter.CreateSourceImporter()],
            sourceFormats: [new ShaderSourceFormat([engineShaders])]);
        await BuildEngineShadersAsync(project, shaders, engineShaders, cancellationToken).ConfigureAwait(false);
        foreach (string source in Directory.EnumerateFiles(shaders, "*.shader").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await project.BuildAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Shader: {Path.GetFileName(source)}");
        }
        await DefaultTextures.BuildAsync(project, engineRoot, cancellationToken).ConfigureAwait(false);
        await DefaultMaterials.BuildAsync(project, cancellationToken).ConfigureAwait(false);
        Guid pipeline = BuildClusterShaders(project);
        Guid shadows = BuildShadowShaders(project);
        IReadOnlyList<Guid> imported = await project.ImportAsync("assets/Models/ico_sphere.glb", cancellationToken).ConfigureAwait(false);
        Guid mesh = imported.Single(id => project.Manifest.Assets[id].AssetType == MeshAsset.FileType);
        Guid scene = await BuildSceneAsync(project, mesh, cancellationToken).ConfigureAwait(false);
        BuildApplicationSettings(project, scene, pipeline, shadows);
        AssetDiagnostic[] errors = project.Validate().Where(static item => item.Severity == AssetDiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Select(static error => error.Message)));
        Console.WriteLine($"Built {project.Manifest.Assets.Count} project assets from project sources and the engine shader library.");
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

    private static async Task BuildEngineShadersAsync(AssetProject project, string shaders,
        string engineShaders, CancellationToken cancellationToken)
    {
        string generated = Path.Combine(shaders, "generated", "EngineSources");
        foreach (string source in Directory.EnumerateFiles(engineShaders, "*.shader").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(source);
            if (File.Exists(Path.Combine(shaders, name))) continue;
            cancellationToken.ThrowIfCancellationRequested();
            string content = File.ReadAllText(source);
            string destination = Path.Combine(generated, name);
            WriteGeneratedFile(destination, content);
            using JsonDocument document = JsonDocument.Parse(content);
            foreach (JsonElement block in document.RootElement.GetProperty("Blocks").EnumerateArray())
            {
                if (!block.GetProperty("Fields").TryGetProperty("SourceNames", out JsonElement names)) continue;
                foreach (JsonElement sourceName in names.EnumerateArray())
                {
                    string sourcePath = sourceName.GetString()!;
                    WriteGeneratedFile(Path.Combine(generated, sourcePath),
                        File.ReadAllText(Path.Combine(engineShaders, sourcePath)));
                }
            }
            await project.BuildAsync(destination, cancellationToken: cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Engine shader: {name}");
        }
    }

    private static string BuildRenderInstanceLayoutSource(RenderInstancePropertyLayout layout)
    {
        int boundsExpansion = layout.Resolve<float>(ClusterRenderFeature.BoundsExpansionKey)
            .Descriptor.MetadataWordOffset;
        int bvhRoot = layout.Resolve<uint>(ClusterRenderFeature.BvhRootKey)
            .Descriptor.MetadataWordOffset;
        int materialSlotOffset = layout.Resolve<uint>(ClusterRenderFeature.MaterialSlotOffsetKey)
            .Descriptor.MetadataWordOffset;
        int depthCacheClass = layout.Resolve<uint>(ClusterRenderFeature.DepthCacheClassKey).Descriptor.MetadataWordOffset;
        int firstPerson = layout.Resolve<FirstPersonMeshData>(ClusterRenderFeature.FirstPersonKey).Descriptor.MetadataWordOffset;
        int currentTransform = FindMetadataWordOffset(
            layout,
            RenderInstanceTransformProperties.CurrentTransformKey);
        int previousTransform = FindMetadataWordOffset(
            layout,
            RenderInstanceTransformProperties.PreviousTransformKey);

        return $$"""
            #ifndef SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED
            #define SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED

            // Dense metadata layout generated from this project's registered Cluster feature. Shader
            // integration must emit one such document for each pipeline/material composition.
            static const uint RENDER_INSTANCE_METADATA_WORD_COUNT = {{layout.MetadataWordCount}}u;
            static const uint RENDER_INSTANCE_METADATA_VECTOR_COUNT =
                (RENDER_INSTANCE_METADATA_WORD_COUNT + 3u) / 4u;

            struct RenderInstancePropertyMetadata
            {
                // Constant-buffer scalar arrays have a 16-byte element stride. Packing into uint4
                // vectors preserves the CPU-side dense uint-word layout.
                uint4 PackedWords[RENDER_INSTANCE_METADATA_VECTOR_COUNT];
            };

            uint LoadRenderInstanceMetadata(
                RenderInstancePropertyMetadata metadata,
                uint wordIndex)
            {
                return metadata.PackedWords[wordIndex >> 2u][wordIndex & 3u];
            }

            static const uint CLUSTER_BOUNDS_EXPANSION_METADATA_WORD = {{boundsExpansion}}u;
            static const uint CLUSTER_BVH_ROOT_METADATA_WORD = {{bvhRoot}}u;
            static const uint CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD = {{materialSlotOffset}}u;
            static const uint RENDER_DEPTH_CACHE_CLASS_METADATA_WORD = {{depthCacheClass}}u;
            static const uint RENDER_FIRST_PERSON_METADATA_WORD = {{firstPerson}}u;
            static const uint RENDER_CURRENT_TRANSFORM_METADATA_WORD = {{currentTransform}}u;
            static const uint RENDER_PREVIOUS_TRANSFORM_METADATA_WORD = {{previousTransform}}u;

            #endif
            """;
    }

    private static int FindMetadataWordOffset(
        RenderInstancePropertyLayout layout,
        RenderInstancePropertyKey key)
    {
        foreach (RenderInstancePropertyDescriptor property in layout.Properties)
        {
            if (property.Key == key)
                return property.MetadataWordOffset;
        }

        throw new InvalidOperationException($"Render-instance property '{key}' is missing from the layout.");
    }

    private static Guid BuildClusterShaders(AssetProject project)
    {
        const string outputPath = "assets/Pipelines/default_cluster.clusterrender.asset";
        using var settings = new ClusterShadersBlock
        {
            Operations =
            [
                ComputeOperation(
                    ClusterShaderOperationRole.BvhTraversal,
                    ResolveRequired(project, "assets/Shaders/cluster_bvh_traverse.shader"),
                    "TraverseHierarchyLevel"),
                ComputeOperation(
                    ClusterShaderOperationRole.LeafFlagFixups,
                    ResolveRequired(project, "assets/Shaders/cluster_leaf_flags.shader"),
                    "ApplyClusterLeafFlags"),
                ComputeOperation(
                    ClusterShaderOperationRole.PageRequests,
                    ResolveRequired(project, "assets/Shaders/cluster_page_request_complete.shader"),
                    "CompleteClusterPageRequests"),
                ComputeOperation(
                    ClusterShaderOperationRole.BvhTraversalInitialize,
                    ResolveRequired(project, "assets/Shaders/cluster_bvh_traverse_seed.shader"),
                    "SeedHierarchyRoots"),
                ComputeOperation(
                    ClusterShaderOperationRole.SceneBounds,
                    ResolveRequired(project, "assets/Shaders/cluster_scene_bounds.shader"),
                    "BuildClusterSceneBounds"),
                ComputeOperation(
                    ClusterShaderOperationRole.BvhDeferredResume,
                    ResolveRequired(project, "assets/Shaders/cluster_deferred_resume.shader"),
                    "ResumeDeferredClusterWork"),
                ComputeOperation(
                    ClusterShaderOperationRole.CullReset,
                    ResolveRequired(project, "assets/Shaders/cluster_cull_reset.shader"),
                    "ClearCull"),
                ComputeOperation(
                    ClusterShaderOperationRole.CullPhaseOne,
                    ResolveRequired(project, "assets/Shaders/cluster_cull_phase1.shader"),
                    "CullPhaseOne"),
                ComputeOperation(
                    ClusterShaderOperationRole.CullPhaseTwo,
                    ResolveRequired(project, "assets/Shaders/cluster_cull_phase2.shader"),
                    "CullPhaseTwo"),
                ComputeOperation(
                    ClusterShaderOperationRole.RasterBinningReset,
                    ResolveRequired(project, "assets/Shaders/cluster_bin_reset.shader"),
                    "ResetBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.RasterBinningCount,
                    ResolveRequired(project, "assets/Shaders/cluster_bin_count.shader"),
                    "CountBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.RasterBinningReserve,
                    ResolveRequired(project, "assets/Shaders/cluster_bin_reserve.shader"),
                    "ReserveBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.RasterBinningScatter,
                    ResolveRequired(project, "assets/Shaders/cluster_bin_scatter.shader"),
                    "ScatterBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.TessellationBinningReset,
                    ResolveRequired(project, "assets/Shaders/cluster_tessellation_bin.shader"),
                    "ResetTessellationBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.TessellationBinningCount,
                    ResolveRequired(project, "assets/Shaders/cluster_tessellation_bin.shader"),
                    "CountTessellationBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.TessellationBinningReserve,
                    ResolveRequired(project, "assets/Shaders/cluster_tessellation_bin.shader"),
                    "ReserveTessellationBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.TessellationBinningScatter,
                    ResolveRequired(project, "assets/Shaders/cluster_tessellation_bin.shader"),
                    "ScatterTessellationBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.SoftwareVisibilityRaster,
                    ResolveRequired(project, "assets/Shaders/sw_raster.shader"),
                    "CSSWRasterBufferAtomic64"),
                RasterOperation(
                    ClusterShaderOperationRole.HardwareVisibilityRaster,
                    ResolveRequired(project, "assets/Shaders/cluster_draw.shader"),
                    "VSVisBuffer",
                    "PSVisBufferHardwareOnly"),
                ComputeOperation(
                    ClusterShaderOperationRole.VisibilityClearImage,
                    ResolveRequired(project, "assets/Shaders/visibility_clear_image.shader"),
                    "ClearVisibilityWinner"),
                ComputeOperation(
                    ClusterShaderOperationRole.VisibilityClearBuffer,
                    ResolveRequired(project, "assets/Shaders/visibility_clear_buffer.shader"),
                    "ClearVisibilityWinner"),
                RasterOperation(
                    ClusterShaderOperationRole.VisibilityResolveImage,
                    ResolveRequired(project, "assets/Shaders/visibility_resolve_image.shader"),
                    "VSFullscreen",
                    "PSResolveVisibilityWinner"),
                RasterOperation(
                    ClusterShaderOperationRole.VisibilityResolveBuffer,
                    ResolveRequired(project, "assets/Shaders/visibility_resolve_buffer.shader"),
                    "VSFullscreen",
                    "PSResolveVisibilityWinner"),
                ComputeOperation(
                    ClusterShaderOperationRole.HiZInitialize,
                    ResolveRequired(project, "assets/Shaders/hiz_init.shader"),
                    "InitHiZ"),
                ComputeOperation(
                    ClusterShaderOperationRole.HiZReduce,
                    ResolveRequired(project, "assets/Shaders/hiz_reduce.shader"),
                    "ReduceHiZ"),
                ComputeOperation(
                    ClusterShaderOperationRole.HiZReducePair,
                    ResolveRequired(project, "assets/Shaders/hiz_reduce_pair.shader"),
                    "ReduceHiZPair"),
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialBinningReset,
                    ResolveRequired(project, "assets/Shaders/shade_bin_reset.shader"),
                    "ResetShadeBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialBinningCount,
                    ResolveRequired(project, "assets/Shaders/shade_bin_count.shader"),
                    "CountShadeBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialBinningReserve,
                    ResolveRequired(project, "assets/Shaders/shade_bin_reserve.shader"),
                    "ReserveShadeBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.MaterialBinningScatter,
                    ResolveRequired(project, "assets/Shaders/shade_bin_scatter.shader"),
                    "ScatterShadeBins"),
                ComputeOperation(
                    ClusterShaderOperationRole.VisibilityResolve,
                    ResolveRequired(project, "assets/Shaders/cluster_resolve.shader"),
                    "CSResolve"),
            ],
        };

        using var asset = new ClusterShadersAsset(DefaultContentIds.ClusterShaders) { Name = "Default Cluster" };
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
        using var asset = new WorldAsset(DefaultContentIds.Scene) { Name = "Default" };
        asset.WriteWorld(world, SceneSerialization.CreateRegistry());
        AssetEntry entry = await asset.SaveAsync(Path.Combine(project.RootPath, outputPath), cancellationToken).ConfigureAwait(false);
        Guid id = project.RegisterAsset(entry);
        Console.WriteLine($"  Scene: {outputPath}");
        return id;
    }

    private static void BuildApplicationSettings(AssetProject project, Guid sceneGuid, Guid clusterPipelineGuid, Guid virtualShadowMapGuid)
    {
        const string outputPath = "assets/Runtime/default.runtime.asset";
        using var settings = new ApplicationSettingsBlock
        {
            Scene = new(sceneGuid), Pipeline = new(clusterPipelineGuid), VirtualShadowMap = new(virtualShadowMapGuid),
            UiShader = new(ResolveRequired(project, "assets/Shaders/imgui.shader")), UiVertexEntry = "VSMain", UiPixelEntry = "PSMain",
            PresentationShader = new(ResolveRequired(project, "assets/Shaders/post_tonemap.shader")), PresentationVertexEntry = "VSMain", PresentationPixelEntry = "PSMain",
            LightingShader = new(ResolveRequired(project, "assets/Shaders/clustered_light_grid.shader")), LightingEntryPoint = "BuildClusteredLightGrid",
            TemporalShader = new(ResolveRequired(project, "assets/Shaders/temporal_resolve.shader")), TemporalVertexEntry = "VSMain", TemporalPixelEntry = "PSMain",
            TranslucencyShader = new(ResolveRequired(project, "assets/Shaders/translucency_composite.shader")), TranslucencyVertexEntry = "VSMain", TranslucencyPixelEntry = "PSMain",
            DebugShader = new(ResolveRequired(project, "assets/Shaders/render_debug_overlay.shader")), DebugVertexEntry = "VSMain", DebugPixelEntry = "PSMain",
            Width = 1280, Height = 720,
        };
        using var asset = new ApplicationSettingsAsset(DefaultContentIds.ApplicationSettings) { Name = "Default Game" };
        asset.SetBlock(settings);
        project.RegisterAsset(asset.SaveAsync(Path.Combine(project.RootPath, outputPath)).AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"  Application: {outputPath}");
    }

    private static ShaderRef ShaderReference(Guid shaderGuid, string entryPoint)
        => new()
        {
            Asset = new(shaderGuid),
            EntryPoint = entryPoint,
        };

    private static Guid BuildShadowShaders(AssetProject project)
    {
        const string outputPath = "assets/Pipelines/default_virtual_shadow.virtualshadow.asset";
        using var settings = new VirtualShadowMapShadersBlock
            {
                ProjectDirectionalLights = ShaderReference(ResolveRequired(project, "assets/Shaders/generated/EngineSources/vsm_project_lights.shader"), "ProjectVirtualShadowDirectionalLights"),
                ProjectLocalLights = ShaderReference(ResolveRequired(project, "assets/Shaders/generated/EngineSources/vsm_project_local_lights.shader"), "ProjectVirtualShadowLocalLights"),
                BlueNoise = new AssetId<TextureAsset>(DefaultContentIds.ShadowBlueNoise),
                FroxelBuild = ShaderReference(ResolveRequired(project, "assets/Shaders/vsm_froxel_build.shader"), "BuildVirtualShadowFroxels"),
                FroxelArguments = ShaderReference(ResolveRequired(project, "assets/Shaders/vsm_froxel_build.shader"), "FinishVirtualShadowFroxelArguments"),
                FroxelMark = ShaderReference(ResolveRequired(project, "assets/Shaders/vsm_froxel_mark.shader"), "MarkVirtualShadowFroxelPages"),
                FroxelPixel = ShaderReference(ResolveRequired(project, "assets/Shaders/vsm_froxel_mark.shader"), "MarkVirtualShadowFroxelPixelReceivers"),
                ResetReceiverMasks = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_receiver_mask_reset.shader"),
                    "ResetVirtualShadowReceiverMasks"),
                ResetFrame = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_frame_reset.shader"),
                    "ResetVirtualShadowFrame"),
                MarkPages = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_mark_pages.shader"),
                    "MarkVirtualShadowPages"),
                MarkCoarsePages = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_mark_coarse_pages.shader"),
                    "MarkVirtualShadowCoarsePages"),
                AllocatePages = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_allocate_pages.shader"),
                    "AllocateVirtualShadowPages"),
                ResidentPages = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_resident_propagation.shader"),
                    "PropagateVirtualShadowResidentPages"),
                Prefilter = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_prefilter.shader"),
                    "PrefilterVirtualShadowDistant"),
                PrefilterReset = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_prefilter_work.shader"),
                    "ResetVirtualShadowFilterWork"),
                PrefilterSelect = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_prefilter_work.shader"),
                    "SelectVirtualShadowFilterPages"),
                PrefilterRequests = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_prefilter_requests.shader"),
                    "PropagateVirtualShadowFilterRequests"),
                MergeStatic = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_merge_static.shader"),
                    "MergeVirtualShadowStaticDepth"),
                ClearPages = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_clear_pages.shader"),
                    "ClearVirtualShadowPages"),
                InitializeState = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_state_init.shader"),
                    "InitializeVirtualShadowState"),
                InvalidateCasters = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_caster_invalidation.shader"),
                    "InvalidateMovedVirtualShadowCasters"),
                BuildPagePool = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_page_pool.shader"),
                    "BuildVirtualShadowPagePool"),
                CompactViews = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_view_compact.shader"),
                    "CompactVirtualShadowViews"),
                InitializePageHiZ = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_page_hiz.shader"),
                    "InitializeVirtualShadowPageHiZ"),
                ReducePageHiZ = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_page_hiz_reduce.shader"),
                    "ReduceVirtualShadowPageHiZ"),
                CompletePageDepth = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_page_depth_complete.shader"),
                    "CompleteVirtualShadowPageDepth"),
                WorkFeedback = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_work_feedback.shader"),
                    "UpdateVirtualShadowWorkThrottle"),
                DynamicResolution = ShaderReference(
                    ResolveRequired(project, "assets/Shaders/vsm_dynamic_resolution.shader"),
                    "UpdateVirtualShadowDynamicResolution"),
            };
        using var asset = new VirtualShadowMapShadersAsset(DefaultContentIds.ShadowShaders) { Name = "Default Virtual Shadow" };
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

    private static Guid ResolveRequired(AssetProject project, string sourcePath) =>
        project.Resolve(sourcePath)
        ?? project.Resolve("assets/Shaders/generated/EngineSources/" + Path.GetFileName(sourcePath))
        ?? throw new InvalidOperationException($"Required asset '{sourcePath}' was not imported.");

}
