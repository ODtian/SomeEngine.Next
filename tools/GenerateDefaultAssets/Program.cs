using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Instances;

string projectRoot = ResolveProjectRoot(Directory.GetCurrentDirectory());
string assetsDir = Path.Combine(projectRoot, "assets");
if (!Directory.Exists(assetsDir))
{
    throw new DirectoryNotFoundException($"Assets directory not found at '{assetsDir}'.");
}

string shadersDir = Path.Combine(assetsDir, "Shaders");
string texturesDir = Path.Combine(assetsDir, "Textures");
string materialsDir = Path.Combine(assetsDir, "Materials");
string scenesDir = Path.Combine(assetsDir, "Scenes");
string runtimeDir = Path.Combine(assetsDir, "Runtime");
Directory.CreateDirectory(texturesDir);
Directory.CreateDirectory(materialsDir);
Directory.CreateDirectory(scenesDir);
Directory.CreateDirectory(runtimeDir);
RenderInstancePropertyLayout instanceProperties = ClusterRenderFeature.InstanceLayout;
WriteGeneratedFile(
    Path.Combine(shadersDir, "generated", "render_instance_layout.slang"),
    BuildRenderInstanceLayoutSource(instanceProperties));

AssetProject project = new AssetProject(projectRoot, [new GltfSourceImporter(), new SlangSourceImporter()]);
if (args.Contains("--cluster-pipeline-only", StringComparer.Ordinal))
{
    GenerateClusterPipeline(project);
    return;
}
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_deform.slang",
    "assets/Shaders/cluster_deform.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_deform_prefix.slang",
    "assets/Shaders/cluster_deform_prefix.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/render_motion.slang",
    "assets/Shaders/render_motion.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_motion_vectors.slang",
    "assets/Shaders/cluster_motion_vectors.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_tessellation_visibility.slang",
    "assets/Shaders/cluster_tessellation_visibility.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_bvh_traverse_indexed.slang",
    "assets/Shaders/cluster_bvh_traverse_indexed.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_virtual_draw.slang",
    "assets/Shaders/cluster_virtual_draw.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_virtual_sw_raster.slang",
    "assets/Shaders/cluster_virtual_sw_raster.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_virtual_tessellation_phong.slang",
    "assets/Shaders/cluster_virtual_tessellation_phong.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_tessellation_split_phong.slang",
    "assets/Shaders/cluster_tessellation_split_phong.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/cluster_tessellation_visibility_phong.slang",
    "assets/Shaders/cluster_tessellation_visibility_phong.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/vsm_page_management.slang",
    "assets/Shaders/vsm_page_management.shader.asset");
RemoveObsoleteAsset(
    projectRoot,
    project,
    "assets/Shaders/vsm_page_coverage.slang",
    "assets/Shaders/vsm_page_coverage.shader.asset");
RemoveGeneratedTestPublications(project);

if (args.Contains("--stamp-vertex-layout-abi", StringComparer.Ordinal))
{
    Console.WriteLine("ShaderData vertex layout protocol metadata is authored on shader entries.");
    return;
}

if (args.Contains("--reimport-icosphere", StringComparer.Ordinal))
{
    await project.ImportAsync("samples/IcoSphere.glb");
    Console.WriteLine("Reimported samples/IcoSphere.glb.");
    return;
}

Guid defaultWhiteTextureGuid = GenerateDefault1x1Texture(
    project,
    "default_white",
    PackRgba(255, 255, 255, 255),
    out TextureData defaultWhiteTexture);
Guid defaultNormalTextureGuid = GenerateDefault1x1Texture(
    project,
    "default_normal",
    PackRgba(128, 128, 255, 255),
    out TextureData defaultNormalTexture
);
Guid defaultArmTextureGuid = GenerateDefault1x1Texture(
    project,
    "default_arm",
    PackRgba(255, 255, 255, 255),
    out TextureData defaultArmTexture);

string[] shaderSources =
[
    "cluster_bin_reset.slang",
    "cluster_bin_count.slang",
    "cluster_bin_reserve.slang",
    "cluster_bin_scatter.slang",
    "cluster_bvh_traverse.slang",
    "cluster_bvh_traverse_seed.slang",
    "cluster_scene_bounds.slang",
    "cluster_deferred_resume.slang",
    "cluster_cull_phase1.slang",
    "cluster_cull_phase2.slang",
    "cluster_cull_reset.slang",
    "cluster_draw.slang",
    "cluster_resolve.slang",
    "shade_bin_reset.slang",
    "shade_bin_count.slang",
    "shade_bin_reserve.slang",
    "shade_bin_scatter.slang",
    "cluster_shade_material.slang",
    "cluster_shade_unlit.slang",
    "cluster_tessellation_bin.slang",
    "cluster_tessellation_phong.slang",
    "clustered_light_grid.slang",
    "debug_args_copy.slang",
    "debug_sphere.slang",
    "visibility_clear_image.slang",
    "visibility_clear_buffer.slang",
    "visibility_resolve_image.slang",
    "visibility_resolve_buffer.slang",
    "hello_triangle.slang",
    "hiz_init.slang",
    "hiz_reduce.slang",
    "hiz_reduce_pair.slang",
    "imgui.slang",
    "post_tonemap.slang",
    "ray_tracing_geometry.slang",
    "render_debug_overlay.slang",
    "shader_contract_probe.slang",
    "stream_output_geometry.slang",
    "sw_raster.slang",
    "temporal_resolve.slang",
    "translucency_composite.slang",
    "triangle.slang",
    "vsm_allocate_pages.slang",
    "vsm_caster_invalidation.slang",
    "vsm_clear_pages.slang",
    "vsm_mark_coarse_pages.slang",
    "vsm_mark_pages.slang",
    "vsm_page_pool.slang",
    "vsm_page_hiz.slang",
    "vsm_state_init.slang",
    "vsm_view_compact.slang",
    "vsm_page_hiz_reduce.slang",
];

foreach (string shaderName in shaderSources)
{
    string shaderPath = Path.Combine(shadersDir, shaderName);
    if (File.Exists(shaderPath))
    {
        await project.ImportAsync(shaderPath);
    }
}

Guid clusterPipelineGuid = GenerateClusterPipeline(project);
Guid virtualShadowMapGuid = GenerateVirtualShadowMapShaders(project);
Guid standardMaterialGuid = GenerateMaterialTemplate(
    project,
    "Standard",
    ResolveRequired(project, "assets/Shaders/cluster_shade_material.slang"),
    defaultWhiteTexture,
    defaultNormalTexture,
    defaultArmTexture,
    standardScalars: true
);
_ = GenerateMaterialTemplate(
    project,
    "TestUnlit_1",
    ResolveRequired(project, "assets/Shaders/cluster_shade_unlit.slang"),
    defaultWhiteTexture,
    defaultNormalTexture,
    defaultArmTexture,
    standardScalars: false
);

string icoSpherePath = Path.Combine(projectRoot, "samples", "IcoSphere.glb");
if (File.Exists(icoSpherePath))
{
    IReadOnlyList<Guid> imported = await project.ImportAsync(icoSpherePath);
    Guid meshGuid = imported.Single(guid =>
        project.Manifest.TryGetAsset(guid, out AssetEntry record)
        && string.Equals(record.AssetType, Asset<Mesh>.FileType, StringComparison.Ordinal));
    Guid sceneGuid = GenerateDefaultScene(project, meshGuid, standardMaterialGuid);
    GenerateRuntimeConfiguration(
        project,
        sceneGuid,
        clusterPipelineGuid,
        virtualShadowMapGuid);
}
else
{
    throw new FileNotFoundException(
        "The generated default scene requires its authored IcoSphere source.",
        icoSpherePath);
}

var diagnostics = project.Validate();
foreach (var diag in diagnostics)
{
    Console.WriteLine($"  [{diag.Severity}] {diag.Kind}: {diag.Message}");
}

if (diagnostics.Any(static d => d.Severity == AssetDiagnosticSeverity.Error))
{
    Console.Error.WriteLine("Asset validation failed with errors.");
    Environment.ExitCode = 1;
    return;
}

foreach (string sourcePath in project.Manifest.Sources.Values)
{
    string fullSourcePath = Path.GetFullPath(Path.Combine(projectRoot, sourcePath));
    SourceMeta meta = SourceMeta.Load(fullSourcePath);
    foreach (SourceOutput output in meta.Outputs.ToArray()) meta.SetOutput(output);
    SourceMeta.Save(fullSourcePath, meta);
}

Console.WriteLine($"Generated default texture assets in: {texturesDir}");
Console.WriteLine($"Generated material templates in: {materialsDir}");
Console.WriteLine($"Generated shader assets in: {shadersDir}");
Console.WriteLine("Generated default render asset.");
Console.WriteLine("Manifest updated successfully.");

static string ResolveProjectRoot(string startPath)
{
    string current = Path.GetFullPath(startPath);
    while (!string.IsNullOrEmpty(current))
    {
        if (
            File.Exists(Path.Combine(current, "SomeEngine.slnx"))
            || File.Exists(Path.Combine(current, "Directory.Build.props"))
        )
        {
            return current;
        }

        string? parent = Path.GetDirectoryName(current);
        if (
            string.IsNullOrEmpty(parent)
            || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)
        )
        {
            break;
        }

        current = parent;
    }

    throw new DirectoryNotFoundException($"Could not locate project root from '{startPath}'.");
}

static void RemoveObsoleteAsset(
    string projectRoot,
    AssetProject project,
    string sourcePath,
    string assetPath)
{
    if (project.Manifest.TryAssetPath(assetPath, out AssetEntry asset))
        project.Manifest.RemoveAsset(asset.AssetGuid);
    if (project.Manifest.TrySourceGuid(sourcePath, out SourceGuid source))
        project.Manifest.RemoveSource(source);

    DeleteObsoleteProjectFile(projectRoot, sourcePath);
    DeleteObsoleteProjectFile(projectRoot, sourcePath + ".meta");
    DeleteObsoleteProjectFile(projectRoot, assetPath);
    DeleteObsoleteProjectFile(projectRoot, assetPath + ".meta");
}

static void DeleteObsoleteProjectFile(string projectRoot, string relativePath)
{
    string fullPath = Path.GetFullPath(Path.Combine(
        projectRoot,
        relativePath.Replace('/', Path.DirectorySeparatorChar)));
    string normalizedRelativePath = Path.GetRelativePath(projectRoot, fullPath);
    if (Path.IsPathRooted(normalizedRelativePath) ||
        normalizedRelativePath.Equals("..", StringComparison.Ordinal) ||
        normalizedRelativePath.StartsWith(
            ".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Obsolete generated asset path '{relativePath}' escapes the project root.");
    }

    File.Delete(fullPath);
}

static void RemoveGeneratedTestPublications(AssetProject project)
{
    Guid[] generated = project.Manifest.Assets.Values
        .Where(static asset => asset.Path.StartsWith(
            "Library/GeneratedTestAssets/",
            StringComparison.Ordinal))
        .Select(static asset => asset.AssetGuid)
        .ToArray();
    foreach (Guid guid in generated)
        project.Manifest.RemoveAsset(guid);
}

static void WriteGeneratedFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
    string normalizedContent = content.Replace("\r\n", "\n");
    if (File.Exists(path) && string.Equals(File.ReadAllText(path), normalizedContent, StringComparison.Ordinal))
    {
        return;
    }

    File.WriteAllText(path, normalizedContent);
}

static string BuildRenderInstanceLayoutSource(RenderInstancePropertyLayout layout)
{
    int boundsExpansion = layout.Resolve<float>(ClusterRenderFeature.BoundsExpansionKey)
        .Descriptor.MetadataWordOffset;
    int bvhRoot = layout.Resolve<uint>(ClusterRenderFeature.BvhRootKey)
        .Descriptor.MetadataWordOffset;
    int materialSlotOffset = layout.Resolve<uint>(ClusterRenderFeature.MaterialSlotOffsetKey)
        .Descriptor.MetadataWordOffset;
    int currentTransform = FindMetadataWordOffset(
        layout,
        RenderInstanceTransformProperties.CurrentTransformKey);
    int previousTransform = FindMetadataWordOffset(
        layout,
        RenderInstanceTransformProperties.PreviousTransformKey);

    return $$"""
        #ifndef SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED
        #define SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED

        // Exact dense metadata layout for the currently cooked Cluster shader variant. ShaderData-cook
        // integration must emit one such document for each pipeline/material composition.
        static const uint RENDER_INSTANCE_METADATA_WORD_COUNT = {{layout.MetadataWordCount}}u;
        static const uint RENDER_INSTANCE_METADATA_VECTOR_COUNT =
            (RENDER_INSTANCE_METADATA_WORD_COUNT + 3u) / 4u;

        struct RenderInstancePropertyMetadata
        {
            // Constant-buffer scalar arrays have a 16-byte element stride. Packing into uint4
            // vectors preserves the CPU-side dense uint-word ABI.
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
        static const uint RENDER_CURRENT_TRANSFORM_METADATA_WORD = {{currentTransform}}u;
        static const uint RENDER_PREVIOUS_TRANSFORM_METADATA_WORD = {{previousTransform}}u;

        #endif
        """;
}

static int FindMetadataWordOffset(
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

static Guid GenerateDefault1x1Texture(
    AssetProject project,
    string name,
    uint rgba,
    out TextureData textureAsset)
{
    byte[] pixels =
    [
        (byte)(rgba & 0xFF),
        (byte)((rgba >> 8) & 0xFF),
        (byte)((rgba >> 16) & 0xFF),
        (byte)((rgba >> 24) & 0xFF),
    ];

    string outputPath = $"assets/Textures/{name}.texture.asset";
    textureAsset = new TextureData
    {
        Name = name,
        Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
        Width = 1,
        Height = 1,
        Depth = 1,
        MipLevelCount = 1,
        ArrayLayerCount = 1,
        Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
        SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
        SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        MipTiles =
        [
            new TextureMipTile
            {
                MipLevel = 0,
                Width = 1,
                Height = 1,
                RowPitch = 4,
                SlicePitch = 4,
                Payload = pixels,
            },
        ],
    };

    Guid guid = project.CreateAsset(outputPath,( textureAsset).ToAsset(project.Resolve(outputPath) ?? default));
    Console.WriteLine($"  TextureData: {outputPath}");
    return guid;
}

static uint PackRgba(byte r, byte g, byte b, byte a)
    => (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

static Guid GenerateMaterialTemplate(
    AssetProject project,
    string name,
    Guid shaderGuid,
    TextureData defaultWhiteTexture,
    TextureData defaultNormalTexture,
    TextureData defaultArmTexture,
    bool standardScalars
)
{
    string outputPath = $"assets/Materials/{name}.material.asset";
    MaterialData material = standardScalars
        ? new StandardMaterialData
        {
            Name = name,
            Shader = new(shaderGuid),
            AlbedoMap = new(Guid.Parse(defaultWhiteTexture.AssetGuid!)),
            NormalMap = new(Guid.Parse(defaultNormalTexture.AssetGuid!)),
            ARMMap = new(Guid.Parse(defaultArmTexture.AssetGuid!)),
            BaseColorTint = System.Numerics.Vector4.One,
            MetallicFactor = 0.0f,
            Roughness = 0.5f,
            EmissiveFactor = System.Numerics.Vector3.Zero,
        }
        : new UnlitMaterialData
        {
            Name = name,
            Shader = new(shaderGuid),
            AlbedoMap = new(Guid.Parse(defaultWhiteTexture.AssetGuid!)),
            BaseColorTint = System.Numerics.Vector4.One,
        };
    Guid guid = project.CreateAsset(outputPath,( material).ToAsset(project.Resolve(outputPath) ?? default));
    Console.WriteLine($"  MaterialData: {outputPath}");
    return guid;
}

static Guid GenerateClusterPipeline(AssetProject project)
{
    const string outputPath = "assets/Pipelines/default_cluster.clusterrender.asset";
    ClusterShadersData asset = new()
    {
        Name = "DefaultCluster",
        Operations =
        [
            ComputeOperation(
                ClusterShaderOperationRole.BvhTraversal,
                ResolveRequired(project, "assets/Shaders/cluster_bvh_traverse.slang"),
                "TraverseHierarchyLevel"),
            ComputeOperation(
                ClusterShaderOperationRole.BvhTraversalInitialize,
                ResolveRequired(project, "assets/Shaders/cluster_bvh_traverse_seed.slang"),
                "SeedHierarchyRoots"),
            ComputeOperation(
                ClusterShaderOperationRole.SceneBounds,
                ResolveRequired(project, "assets/Shaders/cluster_scene_bounds.slang"),
                "BuildClusterSceneBounds"),
            ComputeOperation(
                ClusterShaderOperationRole.BvhDeferredResume,
                ResolveRequired(project, "assets/Shaders/cluster_deferred_resume.slang"),
                "ResumeDeferredClusterWork"),
            ComputeOperation(
                ClusterShaderOperationRole.CullReset,
                ResolveRequired(project, "assets/Shaders/cluster_cull_reset.slang"),
                "ClearCull"),
            ComputeOperation(
                ClusterShaderOperationRole.CullPhaseOne,
                ResolveRequired(project, "assets/Shaders/cluster_cull_phase1.slang"),
                "CullPhaseOne"),
            ComputeOperation(
                ClusterShaderOperationRole.CullPhaseTwo,
                ResolveRequired(project, "assets/Shaders/cluster_cull_phase2.slang"),
                "CullPhaseTwo"),
            ComputeOperation(
                ClusterShaderOperationRole.RasterBinningReset,
                ResolveRequired(project, "assets/Shaders/cluster_bin_reset.slang"),
                "ResetBins"),
            ComputeOperation(
                ClusterShaderOperationRole.RasterBinningCount,
                ResolveRequired(project, "assets/Shaders/cluster_bin_count.slang"),
                "CountBins"),
            ComputeOperation(
                ClusterShaderOperationRole.RasterBinningReserve,
                ResolveRequired(project, "assets/Shaders/cluster_bin_reserve.slang"),
                "ReserveBins"),
            ComputeOperation(
                ClusterShaderOperationRole.RasterBinningScatter,
                ResolveRequired(project, "assets/Shaders/cluster_bin_scatter.slang"),
                "ScatterBins"),
            ComputeOperation(
                ClusterShaderOperationRole.TessellationBinningReset,
                ResolveRequired(project, "assets/Shaders/cluster_tessellation_bin.slang"),
                "ResetTessellationBins"),
            ComputeOperation(
                ClusterShaderOperationRole.TessellationBinningCount,
                ResolveRequired(project, "assets/Shaders/cluster_tessellation_bin.slang"),
                "CountTessellationBins"),
            ComputeOperation(
                ClusterShaderOperationRole.TessellationBinningReserve,
                ResolveRequired(project, "assets/Shaders/cluster_tessellation_bin.slang"),
                "ReserveTessellationBins"),
            ComputeOperation(
                ClusterShaderOperationRole.TessellationBinningScatter,
                ResolveRequired(project, "assets/Shaders/cluster_tessellation_bin.slang"),
                "ScatterTessellationBins"),
            ComputeOperation(
                ClusterShaderOperationRole.SoftwareVisibilityRaster,
                ResolveRequired(project, "assets/Shaders/sw_raster.slang"),
                "CSSWRasterBufferAtomic64"),
            RasterOperation(
                ClusterShaderOperationRole.HardwareVisibilityRaster,
                ResolveRequired(project, "assets/Shaders/cluster_draw.slang"),
                "VSVisBuffer",
                "PSVisBufferHardwareOnly"),
            ComputeOperation(
                ClusterShaderOperationRole.VisibilityClearImage,
                ResolveRequired(project, "assets/Shaders/visibility_clear_image.slang"),
                "ClearVisibilityWinner"),
            ComputeOperation(
                ClusterShaderOperationRole.VisibilityClearBuffer,
                ResolveRequired(project, "assets/Shaders/visibility_clear_buffer.slang"),
                "ClearVisibilityWinner"),
            RasterOperation(
                ClusterShaderOperationRole.VisibilityResolveImage,
                ResolveRequired(project, "assets/Shaders/visibility_resolve_image.slang"),
                "VSFullscreen",
                "PSResolveVisibilityWinner"),
            RasterOperation(
                ClusterShaderOperationRole.VisibilityResolveBuffer,
                ResolveRequired(project, "assets/Shaders/visibility_resolve_buffer.slang"),
                "VSFullscreen",
                "PSResolveVisibilityWinner"),
            ComputeOperation(
                ClusterShaderOperationRole.HiZInitialize,
                ResolveRequired(project, "assets/Shaders/hiz_init.slang"),
                "InitHiZ"),
            ComputeOperation(
                ClusterShaderOperationRole.HiZReduce,
                ResolveRequired(project, "assets/Shaders/hiz_reduce.slang"),
                "ReduceHiZ"),
            ComputeOperation(
                ClusterShaderOperationRole.HiZReducePair,
                ResolveRequired(project, "assets/Shaders/hiz_reduce_pair.slang"),
                "ReduceHiZPair"),
            ComputeOperation(
                ClusterShaderOperationRole.MaterialBinningReset,
                ResolveRequired(project, "assets/Shaders/shade_bin_reset.slang"),
                "ResetShadeBins"),
            ComputeOperation(
                ClusterShaderOperationRole.MaterialBinningCount,
                ResolveRequired(project, "assets/Shaders/shade_bin_count.slang"),
                "CountShadeBins"),
            ComputeOperation(
                ClusterShaderOperationRole.MaterialBinningReserve,
                ResolveRequired(project, "assets/Shaders/shade_bin_reserve.slang"),
                "ReserveShadeBins"),
            ComputeOperation(
                ClusterShaderOperationRole.MaterialBinningScatter,
                ResolveRequired(project, "assets/Shaders/shade_bin_scatter.slang"),
                "ScatterShadeBins"),
            ComputeOperation(
                ClusterShaderOperationRole.VisibilityResolve,
                ResolveRequired(project, "assets/Shaders/cluster_resolve.slang"),
                "CSResolve"),
        ],
    };

    Guid guid = project.CreateAsset(outputPath,( asset).ToAsset(project.Resolve(outputPath) ?? default));
    Console.WriteLine($"  ClusterPipeline: {outputPath}");
    return guid;
}

static Guid GenerateDefaultScene(
    AssetProject project,
    Guid meshGuid,
    Guid materialGuid)
{
    const string outputPath = "assets/Scenes/default.scene.asset";
    const int instanceCount = 1024;
    const int columns = 32;
    const float spacing = 1.5f;
    const float planeZ = 65.0f;
    const float scale = 0.6f;
    float originX = -((columns - 1) * spacing) * 0.5f;
    float originY = -((columns - 1) * spacing) * 0.5f;
    var instances = new List<SceneMeshInstance>(instanceCount);
    for (int index = 0; index < instanceCount; index++)
    {
        int x = index % columns;
        int y = index / columns;
        instances.Add(new SceneMeshInstance
        {
            MeshGuid = meshGuid.ToString(),
            MaterialGuids = [materialGuid.ToString()],
            Position = Vector(
                originX + x * spacing,
                originY + y * spacing,
                planeZ),
            Rotation = new SceneQuaternion { W = 1.0f },
            Scale = Vector(scale, scale, scale),
            BoundsExpansion = 0.3f,
            MotionSeed = unchecked((uint)index * 0x9E3779B9u),
            MotionAmplitude = Vector(
                0.25f + (index & 3) * 0.035f,
                0.12f + ((index >> 2) & 3) * 0.025f,
                0.32f + ((index >> 4) & 3) * 0.04f),
        });
    }

    var scene = new SceneData
    {
        Name = "Default",
        Camera = new SceneCamera
        {
            Position = Vector(0, 0, -3),
            Target = Vector(0, 0, 65),
            Up = Vector(0, 1, 0),
            VerticalFieldOfView = MathF.PI / 4.0f,
            NearPlane = 0.1f,
            FarPlane = 1000.0f,
        },
        MeshInstances = instances,
        DirectionalLights =
        [
            new SceneDirectionalLight
            {
                Direction = Vector(0.35f, -0.85f, 0.4f),
                Color = Vector(1.0f, 0.96f, 0.9f),
                Intensity = 3.0f,
            },
        ],
        PointLights =
        [
            new ScenePointLight
            {
                Position = Vector(0.0f, 6.0f, 52.0f),
                Range = 28.0f,
                Color = Vector(0.55f, 0.75f, 1.0f),
                Intensity = 10.0f,
            },
            new ScenePointLight
            {
                Position = Vector(-18.0f, 4.0f, 58.0f),
                Range = 22.0f,
                Color = Vector(1.0f, 0.55f, 0.45f),
                Intensity = 7.0f,
            },
            new ScenePointLight
            {
                Position = Vector(18.0f, 8.0f, 60.0f),
                Range = 24.0f,
                Color = Vector(0.45f, 1.0f, 0.65f),
                Intensity = 8.0f,
            },
        ],
    };

    Guid guid = project.CreateAsset(outputPath,( scene).ToAsset(project.Resolve(outputPath) ?? default));
    Console.WriteLine($"  Scene: {outputPath}");
    return guid;
}

static void GenerateRuntimeConfiguration(
    AssetProject project,
    Guid sceneGuid,
    Guid clusterPipelineGuid,
    Guid virtualShadowMapGuid)
{
    const string outputPath = "assets/Runtime/default.runtime.asset";
    project.CreateAsset(
        outputPath,(
        new ApplicationData
        {
            Name = "Default Runtime",
            SceneGuid = sceneGuid.ToString(),
            ClusterPipelineGuid = clusterPipelineGuid.ToString(),
            VirtualShadowMapGuid = virtualShadowMapGuid.ToString(),
            UiShaders =
            [
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/imgui.slang"),
                    "VSMain",
                    ShaderStage.Vertex),
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/imgui.slang"),
                    "PSMain",
                    ShaderStage.Pixel),
            ],
            PresentationShaders =
            [
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/post_tonemap.slang"),
                    "VSMain",
                    ShaderStage.Vertex),
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/post_tonemap.slang"),
                    "PSMain",
                    ShaderStage.Pixel),
            ],
            LightingShader = ShaderData(
                ResolveRequired(project, "assets/Shaders/clustered_light_grid.slang"),
                "BuildClusteredLightGrid",
                ShaderStage.Compute),
            TemporalShaders =
            [
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/temporal_resolve.slang"),
                    "VSMain",
                    ShaderStage.Vertex),
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/temporal_resolve.slang"),
                    "PSMain",
                    ShaderStage.Pixel),
            ],
            TranslucencyShaders =
            [
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/translucency_composite.slang"),
                    "VSMain",
                    ShaderStage.Vertex),
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/translucency_composite.slang"),
                    "PSMain",
                    ShaderStage.Pixel),
            ],
            DebugShaders =
            [
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/render_debug_overlay.slang"),
                    "VSMain",
                    ShaderStage.Vertex),
                ShaderData(
                    ResolveRequired(project, "assets/Shaders/render_debug_overlay.slang"),
                    "PSMain",
                    ShaderStage.Pixel),
            ],
            WindowWidth = 1280,
            WindowHeight = 720,
        }).ToAsset(project.Resolve(outputPath) ?? default));
    Console.WriteLine($"  Runtime: {outputPath}");
}

static SceneVector3 Vector(float x, float y, float z) => new() { X = x, Y = y, Z = z };

static ShaderRef ShaderData(Guid shaderGuid, string entryPoint, ShaderStage stage)
    => new()
    {
        AssetGuid = shaderGuid.ToString(),
        EntryPoint = entryPoint,
        Stage = stage,
    };

static Guid GenerateVirtualShadowMapShaders(AssetProject project)
{
    const string outputPath = "assets/Pipelines/default_virtual_shadow.virtualshadow.asset";
    Guid guid = project.CreateAsset(
        outputPath,(
        new VirtualShadowMapShadersData
        {
            Name = "Default Virtual Shadow",
            MarkPages = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_mark_pages.slang"),
                "MarkVirtualShadowPages",
                ShaderStage.Compute),
            MarkCoarsePages = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_mark_coarse_pages.slang"),
                "MarkVirtualShadowCoarsePages",
                ShaderStage.Compute),
            AllocatePages = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_allocate_pages.slang"),
                "AllocateVirtualShadowPages",
                ShaderStage.Compute),
            ClearPages = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_clear_pages.slang"),
                "ClearVirtualShadowPages",
                ShaderStage.Compute),
            InitializeState = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_state_init.slang"),
                "InitializeVirtualShadowState",
                ShaderStage.Compute),
            InvalidateCasters = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_caster_invalidation.slang"),
                "InvalidateMovedVirtualShadowCasters",
                ShaderStage.Compute),
            BuildPagePool = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_page_pool.slang"),
                "BuildVirtualShadowPagePool",
                ShaderStage.Compute),
            CompactViews = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_view_compact.slang"),
                "CompactVirtualShadowViews",
                ShaderStage.Compute),
            InitializePageHiZ = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_page_hiz.slang"),
                "InitializeVirtualShadowPageHiZ",
                ShaderStage.Compute),
            ReducePageHiZ = ShaderData(
                ResolveRequired(project, "assets/Shaders/vsm_page_hiz_reduce.slang"),
                "ReduceVirtualShadowPageHiZ",
                ShaderStage.Compute),
        }).ToAsset(project.Resolve(outputPath) ?? default));
    Console.WriteLine($"  VirtualShadowMap: {outputPath}");
    return guid;
}

static ClusterShaderOperation ComputeOperation(
    ClusterShaderOperationRole role,
    Guid shaderGuid,
    string entryPoint)
    => new()
    {
        Role = role,
        Shaders = [ShaderData(shaderGuid, entryPoint, ShaderStage.Compute)],
    };

static ClusterShaderOperation RasterOperation(
    ClusterShaderOperationRole role,
    Guid shaderGuid,
    string vertexEntryPoint,
    string pixelEntryPoint)
    => new()
    {
        Role = role,
        Shaders =
        [
            ShaderData(shaderGuid, vertexEntryPoint, ShaderStage.Vertex),
            ShaderData(shaderGuid, pixelEntryPoint, ShaderStage.Pixel),
        ],
    };

static Guid ResolveRequired(AssetProject project, string sourcePath) =>
    project.Resolve(sourcePath)
    ?? throw new InvalidOperationException($"Required asset '{sourcePath}' was not imported.");
