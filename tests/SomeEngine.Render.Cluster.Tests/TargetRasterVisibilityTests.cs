using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.Resources;
using SomeEngine.Runtime;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class TargetRasterVisibilityTests
{
    [Theory]
    [InlineData("d3d12", "masked")]
    [InlineData("d3d12", "offset")]
    public async Task DepthTargetEntriesPreserveQuadVisibilityAndUserDepth(string backend, string depthBehavior)
    {
        string root = FrameStageScene.FindRoot();
        using var files = new ShadowEvidence("target-raster-" + backend + "-" + depthBehavior);
        var inputs = new ShadowReferenceInputs(96, 96,
            new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(new(0, 0, -8), new(4, 4, .1f), true),
             new(new(-.25f, .2f, -5.5f), new(.9f), false),
             new(new(.35f, .45f, -6.3f), new(.9f), false)],
            world => world.CreateEntity(new SpotLight(new(-4, 2, -3), 20,
                Vector3.Normalize(new(4, -2, -5)), MathF.Cos(.35f), MathF.Cos(.8f),
                Vector3.One, 30, ShadowSettings: new(SourceRadius: 0))));
        await ShadowReferenceScene.BuildAsync(root, files.Content, inputs);

        string defines = $"#define TARGET_VISIBILITY_DEPTH \"{depthBehavior}\"\n" +
            $"#define TARGET_VISIBILITY_OFFSET {(depthBehavior == "offset" ? 1 : 0)}\n";
        await File.WriteAllTextAsync(Path.Combine(files.Directory, "target.slang"),
            defines + "#include \"target_visibility.slang\"\n");
        await File.WriteAllTextAsync(Path.Combine(files.Directory, "shared.slang"), defines + """
            #define ClusterRasterPixelTarget TestRasterPixelTarget
            #define ClusterRasterMeshPixelTarget TestRasterMeshPixelTarget
            #define ClusterRasterSoftwareTarget TestRasterSoftwareTarget
            #define ClusterRasterSoftwareMeshletTarget TestRasterSoftwareMeshletTarget
            #include "target_visibility.slang"
            """);
        SlangCompileTarget target = backend == "vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil;
        Shader shared = await LoadShaderAsync(root, files.Directory, "shared.slang", target);
        Shader specialized = await LoadShaderAsync(root, files.Directory, "target.slang", target);
        var shadows = new VirtualShadowMapSettings
        {
            VirtualResolution = 2048, AtlasSize = 1024, PageSize = 128, MaxShadowLights = 1,
            DynamicResolutionEnabled = false, LocalReceiverMask = true, MaxFilterRadiusTexels = 0,
        };
        SceneLinearImage expected = await CaptureAsync(files.Content, backend, shared, shadows);
        SceneLinearImage actual = await CaptureAsync(files.Content, backend, specialized, shadows);
        Assert.Contains(expected.Pixels.ToArray().Chunk(4), pixel => pixel[1] == (Half)1 && pixel[0] < (Half).5f);
        Assert.Contains(expected.Pixels.ToArray().Chunk(4), pixel => pixel[1] == (Half)1 && pixel[0] > (Half).9f);
        ImageArtifacts.AssertEqual(new(96, 96, Format.R16G16B16A16Float,
                MemoryMarshal.AsBytes(expected.Pixels.Span).ToArray()),
            new(96, 96, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(actual.Pixels.Span).ToArray()));
        files.Complete();
    }

    private static async Task<Shader> LoadShaderAsync(string root, string directory, string file, SlangCompileTarget target)
    {
        using var source = new ShaderSourceBlock { SourceNames = [file] };
        var description = TestShaderLoading.CreateSessionDescription(target);
        description.SearchPaths = [directory, Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        return await source.CreateAsync(TestShaderLoading.Slang, description, file);
    }

    private static async Task<SceneLinearImage> CaptureAsync(
        string content, string backend, Shader shader, VirtualShadowMapSettings shadows)
    {
        SceneLinearImage? image = null;
        var script = new SceneScript(content, 16,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                    edit.GetMaterial(entity).Shader = shader;
            }),
            new(15, edit => edit.CaptureLinearImage(value => image = value)),
        ]) { ShadowSettings = shadows };
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, "--hardware-raster",
             "--hidden-window", "--no-vsync", "--no-debug-ui", "--no-taa", "--fixed-step", "0.016666667"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        return Assert.IsType<SceneLinearImage>(image);
    }
}
