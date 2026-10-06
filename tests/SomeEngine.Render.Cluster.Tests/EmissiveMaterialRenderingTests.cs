using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class EmissiveMaterialRenderingTests
{
    [Fact]
    public async Task UpdatingEmissiveColorsPreservesTheirRgbComponents()
    {
        string root = FrameStageScene.FindRoot();
        using var source = new ShaderSourceBlock { SourceNames = ["emissive_material.slang"] };
        var description = TestShaderLoading.CreateSessionDescription();
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        var shader = await source.CreateAsync(TestShaderLoading.Slang, description, "Authored emissive RGB");
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true,
                EnableFrameMetricsReadback = true }, plane);
        var material = scene.CreateEmissiveMaterial(shader, new(100, 0, 0));
        scene.UseMaterial(0, material);
        scene.UseMaterial(1, material);
        Vector3[] colors = [new(100, 0, 0), new(0, 100, 0), new(0, 0, 100)];
        foreach (Vector3 color in colors)
        {
            material.EmissiveFactor = color;
            RasterImage image = await scene.ReadNextRasterImageAsync();
            Half[] expected = [(Half)color.X, (Half)color.Y, (Half)color.Z, (Half)1];
            byte[] actual = image.Color.AsSpan((32 * 64 + 32) * 8, 8).ToArray();
            ImageArtifacts.AssertEqual(new(1, 1, Format.R16G16B16A16Float,
                MemoryMarshal.AsBytes(expected.AsSpan()).ToArray()),
                new(1, 1, Format.R16G16B16A16Float, actual));
        }
    }
}
