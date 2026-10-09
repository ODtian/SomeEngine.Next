using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class VertexUvRenderingTests
{
    [Theory]
    [InlineData("D3D12", true)]
    [InlineData("D3D12", false)]
    [InlineData("Vulkan", true)]
    [InlineData("Vulkan", false)]
    public async Task EncodedUvValuesProduceTheAuthoredDepthGradientAcrossTheWholePlane(string backend, bool hardware)
    {
        string root = FrameStageScene.FindRoot();
        using var source = new ShaderSourceBlock { SourceNames = ["vertex_uv_depth.slang"] };
        var description = TestShaderLoading.CreateSessionDescription(backend == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil);
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        var shader = await source.CreateAsync(TestShaderLoading.Slang, description, "User UV depth gradient");
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions { EnableFrameMetricsReadback = true, ForceHardwareRaster = hardware,
            SoftwareRasterEdgeThreshold = 64, MaxCandidates = 65536 };
        await using var scene = await FrameStageScene.CreateAsync(backend, false, options, plane);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100);
        projection.M11 = projection.M22 = 1;
        scene.SetView(new RenderView(Matrix4x4.Identity, projection, 64, 64));
        scene.SetMeshTransform(0, new(new Vector3(0, 0, -2), Quaternion.Identity) { Stretch = new(2, 2, 1) });
        scene.SetMeshTransform(1, new(new Vector3(20, 0, -6), Quaternion.Identity));
        scene.UseMaterial(0, scene.CreateRasterMaterial(shader, Vector4.One));
        RasterImage actual = await scene.ReadNextRasterImageAsync();
        float[] expected = new float[64 * 64];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
            expected[y * 64 + x] = .5f * ((x + .5f) / 64) + .25f * (1 - (y + .5f) / 64);
        ImageArtifacts.AssertEqual(new(64, 64, Format.D32Float, MemoryMarshal.AsBytes(expected.AsSpan()).ToArray()),
            new(64, 64, Format.D32Float, actual.Depth));
    }
}
