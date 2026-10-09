using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class FiniteShadowTessellationTests
{
    [Theory]
    [InlineData("D3D12", DeviceFeatures.None, true)]
    [InlineData("D3D12", DeviceFeatures.MeshShaders, true)]
    [InlineData("D3D12", DeviceFeatures.MeshShaders, false)]
    [InlineData("Vulkan", DeviceFeatures.None, true)]
    [InlineData("Vulkan", DeviceFeatures.MeshShaders, false)]
    public async Task FiniteLinearOffCameraCasterPreservesOriginalTriangleShadowsInFirstAndHistoryFrames(
        string backend, DeviceFeatures optionalFeatures, bool hardware)
    {
        string root = FrameStageScene.FindRoot();
        using var source = new ShaderSourceBlock { SourceNames = ["finite_linear_material.slang"] };
        var description = TestShaderLoading.CreateSessionDescription(backend == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil);
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        var finite = await source.CreateAsync(TestShaderLoading.Slang, description, "Finite linear triangle caster");
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, EnableFrameMetricsReadback = true,
            ForceHardwareRaster = hardware, SoftwareRasterEdgeThreshold = 64, MaxTessellationFactor = 16 };
        var shadows = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 2048,
            MaxShadowLights = 1, DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11, DynamicResolutionEnabled = false };
        await using var original = await FrameStageScene.CreateAsync(backend, true, options, shadowSettings: shadows, optionalFeatures: optionalFeatures);
        await using var tessellated = await FrameStageScene.CreateAsync(backend, true, options, shadowSettings: shadows, optionalFeatures: optionalFeatures);
        foreach (var scene in new[] { original, tessellated })
        {
            scene.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(2, 2, .1f) });
            scene.SetMeshTransform(1, new(new Vector3(0, 4, -4), Quaternion.Identity) { Stretch = new(1.6f, .2f, .5f) });
            scene.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)));
        }
        original.UseMaterial(1, original.CreateRasterMaterial(RasterMaterialKind.Ordinary, Vector4.One));
        tessellated.UseMaterial(1, tessellated.CreateRasterMaterial(finite, Vector4.One));
        for (int frame = 0; frame < 6; frame++)
        {
            if (frame == 3)
                foreach (var scene in new[] { original, tessellated })
                    scene.SetMeshTransform(1, new(new Vector3(.7f, 4, -4), Quaternion.Identity) { Stretch = new(1.6f, .2f, .5f) });
            RasterImage expected = await original.ReadNextRasterImageAsync();
            RasterImage actual = await tessellated.ReadNextRasterImageAsync();
            Assert.True(actual.Metrics.Work.Complete);
            RasterImage.AssertEqual(expected, actual);
        }
        byte[] shadowed = await original.ReadClusterColorAsync();
        original.SetMeshTransform(1, new(new Vector3(20, 4, -4), Quaternion.Identity) { Stretch = new(1.6f, .2f, .5f) });
        Assert.NotEqual(shadowed, await original.ReadClusterColorAsync());
    }
}
