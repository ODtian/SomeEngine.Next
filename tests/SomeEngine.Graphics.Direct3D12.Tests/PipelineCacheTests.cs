using System.Text.Json;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class PipelineCacheTests
{
    private const string ComputeSource = """
        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID) { }
        """;

    private static readonly Lazy<byte[]> ComputeBytecode = new(CompileCompute);

    [Fact]
    public void Warp_persists_and_invalidates_pipeline_library_entries()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP pipeline-library lane must execute on Windows.");
        string directory = Path.Combine(Path.GetTempPath(), $"someengine-pso-{Guid.NewGuid():N}");
        string cachePath = Path.Combine(directory, "warp.d3d12pipeline");
        PipelineCacheKey key = new(Guid.Parse("2e42516f-bbae-4544-bf70-e47978d2747e"), 7);
        PipelineCacheKey survivorKey = new(Guid.Parse("6f6875fe-0cd5-45a7-86ef-87a8bb506438"), 9);
        try
        {
            using (Device first = CreateDevice(cachePath))
            {
                PipelineHandle pipeline = CreateCachedPipeline(first, key);
                _ = CreateCachedPipeline(first, survivorKey);
                Assert.Equal(PipelineStatus.Ready, first.GetPipelineStatus(pipeline));
                PipelineCacheStats stats = first.GetPipelineCacheStats();
                Assert.Equal(2, stats.Misses);
                Assert.Equal(0, stats.Hits);
            }

            Assert.True(File.Exists(cachePath));
            Assert.True(new FileInfo(cachePath).Length > 16);

            using (Device second = CreateDevice(cachePath))
            {
                PipelineHandle loaded = CreateCachedPipeline(second, key);
                Assert.Equal(PipelineStatus.Ready, second.GetPipelineStatus(loaded));
                PipelineCacheStats stats = second.GetPipelineCacheStats();
                Assert.Equal(1, stats.Hits);
                Assert.Equal(0, stats.Misses);

                second.InvalidatePipelineCache(key);
                Assert.Equal(0, second.GetPipelineCacheStats().Entries);
            }

            using (Device third = CreateDevice(cachePath))
            {
                _ = CreateCachedPipeline(third, key);
                _ = CreateCachedPipeline(third, survivorKey);
                PipelineCacheStats stats = third.GetPipelineCacheStats();
                Assert.Equal(1, stats.Hits);
                Assert.Equal(1, stats.Misses);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Identical_portable_keys_do_not_alias_raster_and_compute_pipeline_kinds()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP pipeline cache lane must execute on Windows.");
        using Device device = CreateDevice(path: null);
        PipelineCacheKey key = new(Guid.Parse("9953e590-fc0d-45cb-a936-d3b0d980915e"), 3);

        PipelineHandle raster = CreateCachedPipeline(device, key);
        PipelineHandle compute = CreateCachedComputePipeline(device, key);

        Assert.NotEqual(raster, compute);
        Assert.Equal(PipelineType.Raster, device.GetPipelineMetadata(raster).Type);
        Assert.Equal(PipelineType.Compute, device.GetPipelineMetadata(compute).Type);
        Assert.Equal(2, device.GetPipelineCacheStats().Entries);
        Assert.Equal(raster, CreateCachedPipeline(device, key));
        Assert.Equal(compute, CreateCachedComputePipeline(device, key));
        Assert.Equal(2, device.GetPipelineCacheStats().Hits);
    }

    private static Device CreateDevice(string? path) => new(new Options
    {
        UseWarpAdapter = true,
        EnableDebugLayer = true,
        PipelineCachePath = path,
    });

    private static PipelineHandle CreateCachedPipeline(Device device, PipelineCacheKey key)
    {
        ShaderHandle vertex = device.CreateShader(Shader(
            new ShaderArtifactKey(0xCA01, 0xCA02, 0xCA03, 0xCA04),
            ShaderStage.Vertex,
            "VSMain",
            "triangle.vs.dxil"));
        ShaderHandle pixel = device.CreateShader(Shader(
            new ShaderArtifactKey(0xCB01, 0xCB02, 0xCB03, 0xCB04),
            ShaderStage.Pixel,
            "PSMain",
            "triangle.ps.dxil"));
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            Array.Empty<BindGroupLayoutHandle>(),
            Array.Empty<PushConstantRange>()));
        return device.CreateRasterPipeline(new RasterPipelineDesc(
            layout,
            vertex,
            pixel,
            new[] { Format.R8G8B8A8UNorm },
            Rasterizer: new RasterizerDesc(Cull: CullMode.None),
            BlendAttachments: new[] { new BlendAttachmentDesc() },
            CacheKey: key));
    }

    private static PipelineHandle CreateCachedComputePipeline(Device device, PipelineCacheKey key)
    {
        ShaderHandle shader = device.CreateShader(new ShaderDesc(
            new ShaderArtifactKey(0xCC01, 0xCC02, 0xCC03, 0xCC04),
            ShaderBinaryFormat.Dxil,
            ShaderStage.Compute,
            "CSMain",
            ComputeBytecode.Value,
            new ShaderInterface(Array.Empty<ShaderBinding>(), Array.Empty<PushConstantRange>(), 0xCC01_CC02_CC03_CC04),
            "test:cache-compute"));
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            Array.Empty<BindGroupLayoutHandle>(),
            Array.Empty<PushConstantRange>()));
        return device.CreateComputePipeline(new ComputePipelineDesc(layout, shader, CacheKey: key));
    }

    private static ShaderDesc Shader(
        ShaderArtifactKey key,
        ShaderStage stage,
        string entryPoint,
        string fixture) => new(
            key,
            ShaderBinaryFormat.Dxil,
            stage,
            entryPoint,
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)),
            new ShaderInterface(
                Array.Empty<ShaderBinding>(),
                Array.Empty<PushConstantRange>(),
            1),
            $"test:{entryPoint}");

    private static byte[] CompileCompute()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), $"someengine-pipeline-cache-{Guid.NewGuid():N}");
        string shaderDirectory = Path.Combine(projectRoot, "assets", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        File.WriteAllText(Path.Combine(projectRoot, "Directory.Build.props"), "<Project />");
        string sourcePath = Path.Combine(shaderDirectory, "cache_compute.slang");
        File.WriteAllText(sourcePath, ComputeSource);
        SourceMetaFiles.Save(
            sourcePath,
            new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importer = nameof(SlangShaderImporter),
                ImporterSettings = JsonSerializer.SerializeToElement(
                    new SlangShaderImporterSettings { CookProfile = SlangShaderCookProfiles.D3D12ShaderModel62Name },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }),
            });
        try
        {
            ShaderAsset asset = Assert.IsType<ShaderAsset>(
                Assert.Single(new SlangSourceImporter().Import(projectRoot, sourcePath)).Asset);
            return Assert.Single(
                asset.Variants!,
                static value => value.Backend == "dxil" && value.EntryPoint == "CSMain").Data!.Value.ToArray();
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
        }
    }
}
