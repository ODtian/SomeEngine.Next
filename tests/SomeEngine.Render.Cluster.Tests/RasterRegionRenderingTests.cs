using System.Numerics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class RasterRegionRenderingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Vector4 Red = new(.9f, .1f, .2f, 1);
    private static readonly Vector4 Blue = new(.1f, .2f, .9f, 1);

    public static IEnumerable<object[]> RasterModes =>
    [
        ["D3D12", false], ["D3D12", true], ["Vulkan", false], ["Vulkan", true],
    ];

    public static IEnumerable<object[]> MaterialModes => RasterModes.SelectMany(mode =>
        new[] { RasterMaterialKind.Ordinary, RasterMaterialKind.Tessellated }.Select(kind =>
            new object[] { mode[0], mode[1], kind }));

    [Theory]
    [MemberData(nameof(MaterialModes))]
    public async Task AddingRasterMaterialMatchesFreshInstallation(string backend, bool hardware,
        RasterMaterialKind kind)
    {
        RasterImage expected;
        await using (var fresh = await CreateSceneAsync(backend, hardware))
        {
            fresh.AddMaterialMesh(fresh.CreateRasterMaterial(kind, Blue));
            expected = await fresh.ReadRasterImageAsync();
        }
        await using var changed = await CreateSceneAsync(backend, hardware);
        byte[] previous = await changed.ReadClusterColorAsync();
        changed.AddMaterialMesh(changed.CreateRasterMaterial(kind, Blue));
        RasterImage current = await changed.ReadRasterImageAsync();
        Assert.NotEqual(previous, current.Color);
        AssertSameImage(expected, current);
        if (kind == RasterMaterialKind.Tessellated) Assert.True(current.Metrics.TessellationFinalPatches > 0);
    }

    [Theory]
    [MemberData(nameof(MaterialModes))]
    public async Task RemovingRasterMaterialMatchesFreshInstallation(string backend, bool hardware,
        RasterMaterialKind kind)
    {
        RasterImage expected;
        await using (var fresh = await CreateSceneAsync(backend, hardware))
            expected = await fresh.ReadRasterImageAsync();
        await using var changed = await CreateSceneAsync(backend, hardware);
        var extra = changed.AddMaterialMesh(changed.CreateRasterMaterial(kind, Blue));
        byte[] previous = await changed.ReadClusterColorAsync();
        changed.RemoveMaterialMesh(extra);
        RasterImage current = await changed.ReadRasterImageAsync();
        Assert.NotEqual(previous, current.Color);
        AssertSameImage(expected, current);
        Assert.Equal(0u, current.Metrics.TessellationFinalPatches);
    }

    [Theory]
    [MemberData(nameof(MaterialModes))]
    public async Task ReplacingRasterMaterialMatchesFreshInstallation(string backend, bool hardware,
        RasterMaterialKind kind)
    {
        RasterImage expected;
        await using (var fresh = await CreateSceneAsync(backend, hardware))
        {
            fresh.UseMaterial(1, fresh.CreateRasterMaterial(kind, Blue));
            expected = await fresh.ReadRasterImageAsync();
        }
        await using var changed = await CreateSceneAsync(backend, hardware);
        var previousKind = kind == RasterMaterialKind.Tessellated ? RasterMaterialKind.Ordinary : RasterMaterialKind.Tessellated;
        changed.UseMaterial(1, changed.CreateRasterMaterial(previousKind, Red));
        byte[] previous = await changed.ReadClusterColorAsync();
        changed.UseMaterial(1, changed.CreateRasterMaterial(kind, Blue));
        RasterImage current = await changed.ReadRasterImageAsync();
        Assert.NotEqual(previous, current.Color);
        AssertSameImage(expected, current);
        if (kind == RasterMaterialKind.Tessellated) Assert.True(current.Metrics.TessellationFinalPatches > 0);
    }

    [Theory]
    [MemberData(nameof(RasterModes))]
    public async Task RemovingTheFirstSharedTessellationMaterialMatchesFreshInstallation(string backend, bool hardware)
    {
        RasterImage expected;
        await using (var fresh = await CreateSceneAsync(backend, hardware))
        {
            fresh.UseMaterial(1, fresh.CreateRasterMaterial(RasterMaterialKind.Tessellated, Blue));
            expected = await fresh.ReadRasterImageAsync();
        }
        await using var changed = await CreateSceneAsync(backend, hardware);
        var first = changed.AddMaterialMesh(changed.CreateRasterMaterial(RasterMaterialKind.Tessellated, Red));
        await changed.ReadClusterColorAsync();
        changed.UseMaterial(1, changed.CreateRasterMaterial(RasterMaterialKind.Tessellated, Blue));
        byte[] previous = await changed.ReadClusterColorAsync();
        changed.RemoveMaterialMesh(first);
        RasterImage current = await changed.ReadRasterImageAsync();
        Assert.NotEqual(previous, current.Color);
        AssertSameImage(expected, current);
        Assert.True(current.Metrics.TessellationFinalPatches > 0);
    }

    [Theory]
    [MemberData(nameof(RasterModes))]
    public async Task ReplacingASplitShaderMatchesFreshInstallation(string backend, bool hardware)
    {
        RasterImage expected;
        await using (var fresh = await CreateSceneAsync(backend, hardware))
        {
            fresh.UseMaterial(1, fresh.CreateRasterMaterial(RasterMaterialKind.AdditionalTessellated, Blue));
            expected = await fresh.ReadRasterImageAsync();
        }
        await using var changed = await CreateSceneAsync(backend, hardware);
        changed.UseMaterial(1, changed.CreateRasterMaterial(RasterMaterialKind.Tessellated, Red));
        byte[] previous = await changed.ReadClusterColorAsync();
        changed.UseMaterial(1, changed.CreateRasterMaterial(RasterMaterialKind.AdditionalTessellated, Blue));
        RasterImage current = await changed.ReadRasterImageAsync();
        Assert.NotEqual(previous, current.Color);
        AssertSameImage(expected, current);
        Assert.True(current.Metrics.TessellationFinalPatches > 0);
    }

    [Theory]
    [MemberData(nameof(MaterialModes))]
    public async Task ChangingOneRasterMaterialKeepsIndependentWorkOutsideAnalysis(string backend, bool hardware,
        RasterMaterialKind kind)
    {
        const int independentPasses = 64;
        await using var scene = await CreateSceneAsync(backend, hardware);
        scene.AddIndependentWork(independentPasses);
        Material material = scene.CreateRasterMaterial(kind, Red);
        scene.UseMaterial(1, material);
        byte[] previous = await scene.ReadClusterColorAsync();
        Material replacement = scene.CreateRasterMaterial(kind, Blue);
        material.Update(replacement);
        byte[] current = await scene.ReadClusterColorAsync();
        Assert.NotEqual(previous, current);
        RenderGraphStatistics statistics = scene.ColorStatistics[0];
        Assert.InRange(statistics.AnalyzedPassCount, 1, statistics.DeclaredPassCount - independentPasses);
        output.WriteLine($"Analysis: {statistics.AnalyzedPassCount} analyzed, {statistics.DeclaredPassCount} declared, {independentPasses} independent passes.");
    }

    private static async Task<FrameStageScene> CreateSceneAsync(string backend, bool hardware)
    {
        Mesh mesh = await RasterTestGeometry.CreateAsync();
        return await FrameStageScene.CreateAsync(backend, true, Options(hardware), mesh);
    }

    private static void AssertSameImage(RasterImage expected, RasterImage actual)
    {
        Assert.Equal(0u, expected.Metrics.DroppedTessellationPatches);
        Assert.Equal(0u, actual.Metrics.DroppedTessellationPatches);
        RasterImage.AssertEqual(expected, actual);
    }

    private static ClusterPipelineOptions Options(bool hardware) => new()
    {
        MaxCandidates = 65536,
        EnableFrameMetricsReadback = true,
        MaxTessellationFactor = 16,
        TessellationTargetEdgePixels = 4,
        SoftwareRasterEdgeThreshold = 64,
        ForceHardwareRaster = hardware,
    };
}
