using System.Numerics;
using System.Text.Json;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class VsmWorkRenderingTests
{
    [Theory]
    [InlineData("D3D12", false)]
    [InlineData("D3D12", true)]
    [InlineData("Vulkan", false)]
    [InlineData("Vulkan", true)]
    public async Task TransparentUserVisibilityPreservesTheSameReceiversAsRemovingTheCaster(string backend, bool hardware)
    {
        var options = new ClusterPipelineOptions
        {
            MaxCandidates = 65536, EnableFrameMetricsReadback = true, ForceHardwareRaster = hardware,
            SoftwareRasterEdgeThreshold = 64,
        };
        RasterImage expected;
        await using (var absent = await FrameStageScene.CreateAsync(backend, true, options))
            expected = await absent.ReadRasterImageAsync();
        await using var changed = await FrameStageScene.CreateAsync(backend, true, options);
        var opaque = changed.CreateRasterMaterial(RasterMaterialKind.Ordinary, new Vector4(1, 0, 0, 1));
        var caster = changed.AddMaterialMesh(opaque);
        byte[] opaqueImage = await changed.ReadClusterColorAsync();
        changed.RemoveMaterialMesh(caster);
        changed.AddMaterialMesh(changed.CreateRasterMaterial(RasterMaterialKind.Ordinary, new Vector4(1, 0, 0, 0)));
        RasterImage actual = await changed.ReadRasterImageAsync();
        Assert.NotEqual(expected.Color, opaqueImage);
        Assert.True(actual.Metrics.Work.Complete);
        RasterImage.AssertEqual(expected, actual);
    }

    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public async Task SupportedWorkMemoryPreservesAllShadowedPixels(string backend)
    {
        var shadows = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
            DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
            DynamicResolutionEnabled = false,
        };
        void Configure(FrameStageScene scene)
        {
            scene.SetMeshTransform(0, new(new Vector3(-1.5f, 0, -8), Quaternion.Identity) { Stretch = new(.8f, 2, .1f) });
            scene.AddReceiver(new(new Vector3(1.5f, 0, -8), Quaternion.Identity) { Stretch = new(.8f, 2, .1f) });
            scene.SetMeshTransform(1, new(new Vector3(0, 4, -4), Quaternion.Identity) { Stretch = new(1.6f, .2f, .5f) });
            scene.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)));
        }
        RasterImage expected, firstExpected;
        await using (var normal = await FrameStageScene.CreateAsync(backend, true,
            new ClusterPipelineOptions { MaxCandidates = 16384, EnableFrameMetricsReadback = true }, shadowSettings: shadows))
        {
            Configure(normal);
            firstExpected = await normal.ReadNextRasterImageAsync();
            expected = await normal.ReadRasterImageAsync();
        }
        await using var constrained = await FrameStageScene.CreateAsync(backend, true,
            new ClusterPipelineOptions { MaxCandidates = 4096, EnableFrameMetricsReadback = true }, shadowSettings: shadows);
        Configure(constrained);
        RasterImage firstActual = await constrained.ReadNextRasterImageAsync();
        RasterImage actual = await constrained.ReadRasterImageAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { Backend = backend, First = firstExpected.Metrics,
            FirstConstrained = firstActual.Metrics, Settled = expected.Metrics, Constrained = actual.Metrics }));
        Assert.True(firstExpected.Metrics.Work.Complete);
        Assert.True(firstActual.Metrics.Work.Complete);
        RasterImage.AssertEqual(firstExpected, firstActual);
        Assert.True(expected.Metrics.Work.Complete);
        Assert.True(actual.Metrics.Work.Complete);
        RasterImage.AssertEqual(expected, actual);
    }
}
