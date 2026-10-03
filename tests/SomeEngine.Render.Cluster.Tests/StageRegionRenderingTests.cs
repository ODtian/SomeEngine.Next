using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class StageRegionRenderingTests
{
    public static IEnumerable<object[]> Backends => [["D3D12"], ["Vulkan"]];
    private static readonly Vector4 Blue = new(.1f, .2f, .9f, 1);

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AddedHierarchyLevelsProduceGeometryInTheirFirstFrame(string backend)
    {
        var deep = await RasterTestGeometry.CreateDeepAsync();
        RasterImage expected;
        await using (var fresh = await CreateSceneAsync(backend))
        {
            fresh.UseMaterial("Unlit");
            fresh.AddGeometryMesh(deep);
            expected = await fresh.ReadRasterImageAsync();
        }
        await using var changed = await CreateSceneAsync(backend);
        changed.UseMaterial("Unlit");
        RasterImage before = await changed.ReadRasterImageAsync();
        changed.AddGeometryMesh(deep);
        RasterImage first = await changed.ReadNextRasterImageAsync();
        Assert.NotEqual(before.Depth, first.Depth);
        Assert.Equal(expected.Color, first.Color);
        Assert.Equal(expected.Depth, first.Depth);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task GeometryRestoredAfterAnEmptyFrameMatchesFreshInstallation(string backend)
    {
        await using var scene = await CreateSceneAsync(backend);
        RasterImage expected = await scene.ReadRasterImageAsync();
        scene.SetGeometryVisible(false);
        byte[] empty = await scene.ReadClusterColorAsync();
        ReadOnlySpan<Half> pixels = MemoryMarshal.Cast<byte, Half>(empty);
        for (int pixel = 0; pixel < pixels.Length; pixel += 4)
        {
            Assert.Equal((Half)0, pixels[pixel]);
            Assert.Equal((Half)0, pixels[pixel + 1]);
            Assert.Equal((Half)0, pixels[pixel + 2]);
            Assert.Equal((Half)1, pixels[pixel + 3]);
        }
        scene.SetGeometryVisible(true);
        RasterImage restored = await scene.ReadRasterImageAsync();
        Assert.Equal(expected.Color, restored.Color);
        Assert.Equal(expected.Depth, restored.Depth);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AddingAShadeMaterialMatchesFreshInstallation(string backend)
    {
        RasterImage expected;
        await using (var fresh = await CreateSceneAsync(backend))
        {
            fresh.AddMaterialMesh(fresh.CreateShadeMaterial(Blue));
            expected = await fresh.ReadRasterImageAsync();
        }
        await using var changed = await CreateSceneAsync(backend);
        await changed.ReadRasterImageAsync();
        changed.AddMaterialMesh(changed.CreateShadeMaterial(Blue));
        RasterImage current = await changed.ReadRasterImageAsync();
        Assert.Equal(expected.Color, current.Color);
        Assert.Equal(expected.Depth, current.Depth);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task RemovingAShadeMaterialMatchesFreshInstallation(string backend)
    {
        RasterImage expected;
        await using (var fresh = await CreateSceneAsync(backend)) expected = await fresh.ReadRasterImageAsync();
        await using var changed = await CreateSceneAsync(backend);
        var added = changed.AddMaterialMesh(changed.CreateShadeMaterial(Blue));
        await changed.ReadRasterImageAsync();
        changed.RemoveMaterialMesh(added);
        RasterImage current = await changed.ReadRasterImageAsync();
        Assert.Equal(expected.Color, current.Color);
        Assert.Equal(expected.Depth, current.Depth);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task RemovingAMaterialWhoseShaderFailsToLinkRestoresThePreviousOutput(string backend)
    {
        await using var scene = await CreateSceneAsync(backend);
        RasterImage before = await scene.ReadRasterImageAsync();
        var material = scene.CreateShadeMaterial(Blue);
        material.Shader = RasterTestShaders.CreateWithUnresolvedShade(backend);
        var added = scene.AddMaterialMesh(material);
        var error = await Assert.ThrowsAsync<GraphicsException>(() => scene.ReadNextRasterImageAsync());
        Assert.Contains(backend == "Vulkan" ? "SPIR-V" : "DXIL", error.Message);
        scene.RemoveMaterialMesh(added);
        RasterImage restored = await scene.ReadRasterImageAsync();
        Assert.Equal(before.Color, restored.Color);
        Assert.Equal(before.Depth, restored.Depth);
    }

    private static async Task<FrameStageScene> CreateSceneAsync(string backend) =>
        await FrameStageScene.CreateAsync(backend, true,
            new ClusterPipelineOptions { MaxCandidates = 65536, EnableFrameMetricsReadback = true },
            await RasterTestGeometry.CreateAsync());
}
