using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class RasterFaceRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserTwoSidedMaterialPreservesBackFaceRgb(bool hardware)
    {
        string root = FrameStageScene.FindRoot();
        using var source = new ShaderSourceBlock { SourceNames = ["both_face_emissive.slang"] };
        var description = TestShaderLoading.CreateSessionDescription();
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        var shader = await source.CreateAsync(TestShaderLoading.Slang, description, "User two-sided emissive RGB");
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = hardware,
                SoftwareRasterEdgeThreshold = 65536 }, plane);
        var material = scene.CreateEmissiveMaterial(shader, new(3, 1, 2));
        scene.UseMaterial(0, material);
        scene.UseMaterial(1, material);
        scene.SetMeshTransform(0, new(new Vector3(20, 0, -8), Quaternion.Identity));
        scene.SetMeshTransform(1, new(new Vector3(0, 0, -6), Quaternion.Identity));
        byte[] front = await scene.ReadClusterColorAsync();
        scene.SetMeshTransform(1, new(new Vector3(0, 0, -6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)));
        byte[] back = await scene.ReadClusterColorAsync();
        Half[] rgb = [(Half)3, (Half)1, (Half)2, (Half)1];
        ImageArtifacts.AssertEqual(new(1, 1, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(rgb.AsSpan()).ToArray()),
            new(1, 1, Format.R16G16B16A16Float, front.AsSpan((32 * 64 + 32) * 8, 8).ToArray()));
        ImageArtifacts.AssertEqual(new(1, 1, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(rgb.AsSpan()).ToArray()),
            new(1, 1, Format.R16G16B16A16Float, back.AsSpan((32 * 64 + 32) * 8, 8).ToArray()));
    }
}
