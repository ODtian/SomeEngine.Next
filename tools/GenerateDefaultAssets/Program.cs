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

AssetProject project = AssetAuthoring.CreateProject(projectRoot);

if (args.Contains("--stamp-vertex-layout-abi", StringComparer.Ordinal))
{
    Console.WriteLine("Shader vertex layout protocol metadata is authored on shader entries.");
    return;
}

if (args.Contains("--reimport-icosphere", StringComparer.Ordinal))
{
    await project.ImportAsync("samples/IcoSphere.glb");
    Console.WriteLine("Reimported samples/IcoSphere.glb.");
    return;
}

AssetGuid defaultWhiteTextureGuid = GenerateDefault1x1Texture(project, "default_white", PackRgba(255, 255, 255, 255));
AssetGuid defaultNormalTextureGuid = GenerateDefault1x1Texture(
    project,
    "default_normal",
    PackRgba(128, 128, 255, 255)
);
AssetGuid defaultArmTextureGuid = GenerateDefault1x1Texture(project, "default_arm", PackRgba(255, 255, 255, 255));

string[] shaderSources =
[
    "cluster_binning.slang",
    "cluster_bvh_traverse.slang",
    "cluster_cull.slang",
    "cluster_deform.slang",
    "cluster_draw.slang",
    "cluster_motion_vectors.slang",
    "cluster_resolve.slang",
    "cluster_shade_binning.slang",
    "cluster_shade_material.slang",
    "cluster_shade_unlit.slang",
    "debug_args_copy.slang",
    "debug_sphere.slang",
    "depth_merge.slang",
    "hello_triangle.slang",
    "hiz_build.slang",
    "imgui.slang",
    "post_tonemap.slang",
    "shader_contract_probe.slang",
    "sw_raster.slang",
    "temporal_resolve.slang",
    "triangle.slang",
];

foreach (string shaderName in shaderSources)
{
    string shaderPath = Path.Combine(shadersDir, shaderName);
    if (File.Exists(shaderPath))
    {
        await project.ImportAsync(shaderPath);
    }
}

AssetGuid clusterRendererGuid = GenerateClusterPipeline(project);
AssetGuid defaultPbrMaterialGuid = GenerateMaterialTemplate(
    project,
    "DefaultPBR",
    shadeShaderGuid: ResolveRequired(project, "assets/Shaders/cluster_shade_material.slang"),
    shadeEntryPoint: "CSMaterialShadeCached",
    defaultWhiteTextureGuid,
    defaultNormalTextureGuid,
    defaultArmTextureGuid,
    pbrScalars: true
);
_ = GenerateMaterialTemplate(
    project,
    "TestUnlit_1",
    shadeShaderGuid: ResolveRequired(project, "assets/Shaders/cluster_shade_unlit.slang"),
    shadeEntryPoint: "CSUnlitShadeCached",
    defaultWhiteTextureGuid,
    defaultNormalTextureGuid,
    defaultArmTextureGuid,
    pbrScalars: false
);

string icoSpherePath = Path.Combine(projectRoot, "samples", "IcoSphere.glb");
if (File.Exists(icoSpherePath))
{
    IReadOnlyList<AssetGuid> imported = await project.ImportAsync(icoSpherePath);
    AssetGuid meshGuid = imported.Single(guid =>
        project.Manifest.TryGetAsset(guid, out AssetManifestRecord record)
        && string.Equals(record.AssetType, AssetType<Mesh>.Name, StringComparison.Ordinal));
    AssetGuid sceneGuid = GenerateDefaultScene(project, meshGuid, defaultPbrMaterialGuid);
    GenerateRuntimeConfiguration(project, sceneGuid, clusterRendererGuid);
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

        // Exact dense metadata layout for the currently cooked Cluster shader variant. Shader-cook
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

static AssetGuid GenerateDefault1x1Texture(AssetProject project, string name, uint rgba)
{
    byte[] pixels =
    [
        (byte)(rgba & 0xFF),
        (byte)((rgba >> 8) & 0xFF),
        (byte)((rgba >> 16) & 0xFF),
        (byte)((rgba >> 24) & 0xFF),
    ];

    string outputPath = $"assets/Textures/{name}.texture.asset";
    var textureAsset = new Texture
    {
        Name = name,
        Width = 1,
        Height = 1,
        Format = "RGBA8_UNorm",
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

    AssetGuid guid = project.CreateAsset(outputPath, textureAsset);
    Console.WriteLine($"  Texture: {outputPath}");
    return guid;
}

static uint PackRgba(byte r, byte g, byte b, byte a)
    => (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

static AssetGuid GenerateMaterialTemplate(
    AssetProject project,
    string name,
    AssetGuid shadeShaderGuid,
    string shadeEntryPoint,
    AssetGuid defaultWhiteTextureGuid,
    AssetGuid defaultNormalTextureGuid,
    AssetGuid defaultArmTextureGuid,
    bool pbrScalars
)
{
    string outputPath = $"assets/Materials/{name}.material.asset";

    AssetGuid guid = project.CreateAsset(
        outputPath,
        new Material
        {
            Name = name,
            Passes =
            [
                new PassEntry
                {
                    Shader = Shader(shadeShaderGuid, shadeEntryPoint, ShaderStage.Compute),
                    Tags = [new TagEntry { Name = "opaque" }],
                },
            ],
            Textures = CreateDefaultTextureBindings(
                defaultWhiteTextureGuid,
                defaultNormalTextureGuid,
                defaultArmTextureGuid,
                pbrScalars
            ),
            Scalars = CreateDefaultScalars(pbrScalars),
        }
    );
    Console.WriteLine($"  Material: {outputPath}");
    return guid;
}

static AssetGuid GenerateClusterPipeline(AssetProject project)
{
    const string outputPath = "assets/Pipelines/default_cluster.clusterrender.asset";
    ClusterShaders asset = new()
    {
        Name = "DefaultCluster",
        Operations =
        [
            ComputeOperation(
                ClusterShaderOperationRole.BvhTraversal,
                ResolveRequired(project, "assets/Shaders/cluster_bvh_traverse.slang"),
                "main"),
            ComputeOperation(
                ClusterShaderOperationRole.CullPhaseOneReset,
                ResolveRequired(project, "assets/Shaders/cluster_cull.slang"),
                "clear_main_phase1"),
            ComputeOperation(
                ClusterShaderOperationRole.CullPhaseOne,
                ResolveRequired(project, "assets/Shaders/cluster_cull.slang"),
                "main_phase1"),
            ComputeOperation(
                ClusterShaderOperationRole.CullPhaseTwoReset,
                ResolveRequired(project, "assets/Shaders/cluster_cull.slang"),
                "clear_main_phase2"),
            ComputeOperation(
                ClusterShaderOperationRole.CullPhaseTwo,
                ResolveRequired(project, "assets/Shaders/cluster_cull.slang"),
                "main_phase2"),
            ComputeOperation(
                ClusterShaderOperationRole.RasterDeformBinningReset,
                ResolveRequired(project, "assets/Shaders/cluster_binning.slang"),
                "ResetRasterDeformBins"),
            ComputeOperation(
                ClusterShaderOperationRole.RasterDeformBinningCount,
                ResolveRequired(project, "assets/Shaders/cluster_binning.slang"),
                "CountRasterDeformBins"),
            ComputeOperation(
                ClusterShaderOperationRole.RasterDeformBinningReserve,
                ResolveRequired(project, "assets/Shaders/cluster_binning.slang"),
                "ReserveRasterDeformBins"),
            ComputeOperation(
                ClusterShaderOperationRole.RasterDeformBinningScatter,
                ResolveRequired(project, "assets/Shaders/cluster_binning.slang"),
                "ScatterRasterDeformBins"),
            ComputeOperation(
                ClusterShaderOperationRole.DeformCachePopulate,
                ResolveRequired(project, "assets/Shaders/cluster_deform.slang"),
                "CSDeformWave"),
            ComputeOperation(
                ClusterShaderOperationRole.SoftwareVisibilityRaster,
                ResolveRequired(project, "assets/Shaders/sw_raster.slang"),
                "CSSWRasterCached"),
            RasterOperation(
                ClusterShaderOperationRole.HardwareVisibilityRaster,
                ResolveRequired(project, "assets/Shaders/cluster_draw.slang"),
                "VSVisBufferCached",
                "PSVisBuffer"),
            RasterOperation(
                ClusterShaderOperationRole.SoftwareDepthMerge,
                ResolveRequired(project, "assets/Shaders/depth_merge.slang"),
                "VSFullscreen",
                "PSDepthMerge"),
            ComputeOperation(
                ClusterShaderOperationRole.HiZInitialize,
                ResolveRequired(project, "assets/Shaders/hiz_build.slang"),
                "BuildMip0And1"),
            ComputeOperation(
                ClusterShaderOperationRole.HiZReduce,
                ResolveRequired(project, "assets/Shaders/hiz_build.slang"),
                "DownsampleMip"),
            ComputeOperation(
                ClusterShaderOperationRole.HiZReducePair,
                ResolveRequired(project, "assets/Shaders/hiz_build.slang"),
                "DownsampleTwoMips"),
            ComputeOperation(
                ClusterShaderOperationRole.MaterialBinningReset,
                ResolveRequired(project, "assets/Shaders/cluster_shade_binning.slang"),
                "CSBinClearPrepare"),
            ComputeOperation(
                ClusterShaderOperationRole.MaterialBinningCount,
                ResolveRequired(project, "assets/Shaders/cluster_shade_binning.slang"),
                "CSBinCount"),
            ComputeOperation(
                ClusterShaderOperationRole.MaterialBinningReserve,
                ResolveRequired(project, "assets/Shaders/cluster_shade_binning.slang"),
                "CSBinReserve"),
            ComputeOperation(
                ClusterShaderOperationRole.MaterialBinningScatter,
                ResolveRequired(project, "assets/Shaders/cluster_shade_binning.slang"),
                "CSBinScatter"),
            ComputeOperation(
                ClusterShaderOperationRole.MotionVectors,
                ResolveRequired(project, "assets/Shaders/cluster_motion_vectors.slang"),
                "CSMotionVectors"),
            ComputeOperation(
                ClusterShaderOperationRole.VisibilityResolve,
                ResolveRequired(project, "assets/Shaders/cluster_resolve.slang"),
                "CSResolve"),
            RasterOperation(
                ClusterShaderOperationRole.TemporalResolve,
                ResolveRequired(project, "assets/Shaders/temporal_resolve.slang"),
                "VSMain",
                "PSMain"),
            RasterOperation(
                ClusterShaderOperationRole.ToneMapAndPresent,
                ResolveRequired(project, "assets/Shaders/post_tonemap.slang"),
                "VSMain",
                "PSMain"),
        ],
    };

    AssetGuid guid = project.CreateAsset(outputPath, asset);
    Console.WriteLine($"  ClusterPipeline: {outputPath}");
    return guid;
}

static AssetGuid GenerateDefaultScene(
    AssetProject project,
    AssetGuid meshGuid,
    AssetGuid materialGuid)
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
            MeshGuid = meshGuid.ToFlatString(),
            MaterialGuids = [materialGuid.ToFlatString()],
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

    var scene = new RenderScene
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

    AssetGuid guid = project.CreateAsset(outputPath, scene);
    Console.WriteLine($"  Scene: {outputPath}");
    return guid;
}

static void GenerateRuntimeConfiguration(
    AssetProject project,
    AssetGuid sceneGuid,
    AssetGuid clusterRendererGuid)
{
    const string outputPath = "assets/Runtime/default.runtime.asset";
    project.CreateAsset(
        outputPath,
        new RuntimeConfiguration
        {
            Name = "Default Runtime",
            SceneGuid = sceneGuid.ToFlatString(),
            ClusterRendererGuid = clusterRendererGuid.ToFlatString(),
            UiShaders =
            [
                Shader(
                    ResolveRequired(project, "assets/Shaders/imgui.slang"),
                    "VSMain",
                    ShaderStage.Vertex),
                Shader(
                    ResolveRequired(project, "assets/Shaders/imgui.slang"),
                    "PSMain",
                    ShaderStage.Pixel),
            ],
            WindowWidth = 1280,
            WindowHeight = 720,
        });
    Console.WriteLine($"  Runtime: {outputPath}");
}

static SceneVector3 Vector(float x, float y, float z) => new() { X = x, Y = y, Z = z };

static ShaderRef Shader(AssetGuid shaderGuid, string entryPoint, ShaderStage stage)
    => new()
    {
        AssetGuid = shaderGuid.ToFlatString(),
        EntryPoint = entryPoint,
        Stage = stage,
    };

static ClusterShaderOperation ComputeOperation(
    ClusterShaderOperationRole role,
    AssetGuid shaderGuid,
    string entryPoint)
    => new()
    {
        Role = role,
        Shaders = [Shader(shaderGuid, entryPoint, ShaderStage.Compute)],
    };

static ClusterShaderOperation RasterOperation(
    ClusterShaderOperationRole role,
    AssetGuid shaderGuid,
    string vertexEntryPoint,
    string pixelEntryPoint)
    => new()
    {
        Role = role,
        Shaders =
        [
            Shader(shaderGuid, vertexEntryPoint, ShaderStage.Vertex),
            Shader(shaderGuid, pixelEntryPoint, ShaderStage.Pixel),
        ],
    };

static AssetGuid ResolveRequired(AssetProject project, string sourcePath) =>
    project.Resolve(sourcePath)
    ?? throw new InvalidOperationException($"Required asset '{sourcePath}' was not imported.");

static List<TextureBinding> CreateDefaultTextureBindings(
    AssetGuid defaultWhiteTextureGuid,
    AssetGuid defaultNormalTextureGuid,
    AssetGuid defaultArmTextureGuid,
    bool pbr
)
{
    List<TextureBinding> textures =
    [
        new() { Name = "AlbedoMap", TextureGuid = defaultWhiteTextureGuid.ToFlatString() },
    ];

    if (pbr)
    {
        textures.Add(
            new TextureBinding
            {
                Name = "NormalMap",
                TextureGuid = defaultNormalTextureGuid.ToFlatString(),
            }
        );
        textures.Add(
            new TextureBinding { Name = "ARMMap", TextureGuid = defaultArmTextureGuid.ToFlatString() }
        );
    }

    return textures;
}

static List<ScalarParam> CreateDefaultScalars(bool pbr)
{
    List<ScalarParam> scalars =
    [
        new()
        {
            Name = "BaseColorTint",
            Value = new ParamValue(
                new Vec4Val
                {
                    X = 1.0f,
                    Y = 1.0f,
                    Z = 1.0f,
                    W = 1.0f,
                }
            ),
        },
    ];

    if (pbr)
    {
        scalars.Add(
            new ScalarParam
            {
                Name = "MetallicFactor",
                Value = new ParamValue(new FloatVal { V = 0.0f }),
            }
        );
        scalars.Add(
            new ScalarParam
            {
                Name = "Roughness",
                Value = new ParamValue(new FloatVal { V = 0.5f }),
            }
        );
        scalars.Add(
            new ScalarParam
            {
                Name = "EmissiveFactor",
                Value = new ParamValue(
                    new Vec3Val
                    {
                        X = 0.0f,
                        Y = 0.0f,
                        Z = 0.0f,
                    }
                ),
            }
        );
    }

    return scalars;
}
