using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using SomeEngine.Runtime;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class SurfaceDepthRenderingTests
{
    [Fact]
    public async Task SpecializedSurfacePreservesAuthoredDepthAndReceiverPosition()
    {
        string root = FrameStageScene.FindRoot();
        using var files = new ShadowEvidence("surface-depth-specialization");
        var inputs = new ShadowReferenceInputs(64, 64,
            new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(new(0, 0, -8), new(4, 4, .1f), true)],
            world => world.CreateEntity(new PointLight(new(0, 3, -3), 20, Vector3.One, 1)));
        await ShadowReferenceScene.BuildAsync(root, files.Content, inputs);
        await File.WriteAllTextAsync(Path.Combine(files.Directory, "surface.slang"), SurfaceSource);
        await File.WriteAllTextAsync(Path.Combine(files.Directory, "ordinary.slang"),
            "#include \"surface.slang\"\n");
        await File.WriteAllTextAsync(Path.Combine(files.Directory, "specialized.slang"),
            "#define CLUSTER_SURFACE_VISIBILITY_DEPTH 1\n#define ClusterSurfaceData UnusedSurfaceData\n#include \"surface.slang\"\n");
        var description = TestShaderLoading.CreateSessionDescription(SlangCompileTarget.Dxil);
        description.SearchPaths = [files.Directory, Path.Combine(root, "templates/DefaultProject/assets/Shaders"),
            Path.Combine(root, "assets/Shaders")];
        using var ordinarySource = new ShaderSourceBlock { SourceNames = ["ordinary.slang"] };
        Shader ordinary = await ordinarySource.CreateAsync(TestShaderLoading.Slang, description, "Ordinary surface");
        using var specializedSource = new ShaderSourceBlock
        {
            SourceNames = ["specialized.slang"],
            Specializations = [new ShaderSpecialization { EntryPoint = "ClusterSurfaceDataDepth", Arguments = ["2"] }],
        };
        Shader specialized = await specializedSource.CreateAsync(TestShaderLoading.Slang, description, "Specialized surface");
        SceneLinearImage expected = await CaptureAsync(files.Content, ordinary);
        SceneLinearImage actual = await CaptureAsync(files.Content, specialized);
        Assert.Contains(expected.Pixels.ToArray().Chunk(4), pixel => pixel[0] == (Half)1 && pixel[1] > (Half).6f);
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(expected.Pixels.Span).ToArray()),
            new(64, 64, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(actual.Pixels.Span).ToArray()));
        files.Complete();
    }

    private static async Task<SceneLinearImage> CaptureAsync(string content, Shader shader)
    {
        SceneLinearImage? image = null;
        var script = new SceneScript(content, 16,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities) edit.GetMaterial(entity).Shader = shader;
            }),
            new(15, edit => edit.CaptureLinearImage(value => image = value)),
        ]) { RasterEdgePixels = 4096 };
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", "d3d12", "--no-rhi-validation", "--hidden-window",
             "--no-vsync", "--no-debug-ui", "--no-taa", "--fixed-step", "0.016666667"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        return Assert.IsType<SceneLinearImage>(image);
    }

    private const string SurfaceSource = """
        #include "generated/render_instance_layout.slang"
        #include "cluster_shade_pipeline.slang"
        #include "triangle_surface.slang"

        [VisibilityDepth("offset")]
        struct AuthoredDepthVisibility : IVisibilityEvaluate<StandardVertex>
        {
            typealias Varying = EmptyVisibilityVarying;
            Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input) { return {}; }
            Varying readLane(Varying value, uint lane) { return {}; }
            Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary) { return {}; }
            Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input) { return {}; }
            VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
            {
                VisibilityPixelResult result;
                result.Visible = 1u;
                result.Depth = saturate(input.Depth + 0.002);
                return result;
            }
        };

        [MaterialSurfaceData]
        struct DepthSurfaceData { float3 Color; };
        struct AuthoredDepthSurface : ISurfaceEvaluate<SurfaceEval, Triangle>
        {
            typealias Data = DepthSurfaceData;
            Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
                Triangle triangle, out SurfaceReceiver receiver)
            {
                receiver = (SurfaceReceiver)0;
                receiver.Position = pixel.VisibilityDepth == 2u ? pixel.PositionFromDepth : surface.Position;
                receiver.Depth = pixel.Depth;
                receiver.ShadingNormal = surface.Normal;
                receiver.GeometricNormal = surface.Normal;
                ClusterViewUniforms view = Views[pixel.PixelCoord.z];
                float4 clip = mul(float4(ClusterPositionForView(surface.Position, view), 1.0), view.ViewProj);
                receiver.DepthOffset = pixel.VisibilityDepth == 2u ? pixel.Depth - clip.z / clip.w : 0.0;
                Data data;
                data.Color = float3(float(pixel.VisibilityDepth) * 0.5,
                    0.5 + receiver.DepthOffset * 100.0, 0.5 + receiver.Position.z * 0.02);
                return data;
            }
            float4 shadeSurface(uint3 pixel, uint debugMode, Data data, SurfaceReceiver receiver)
            { return float4(data.Color, 1.0); }
        };

        typedef StaticVertexEval VertexEvaluate;
        typedef LinearSurface DomainEvaluate;
        typedef AuthoredDepthVisibility VisibilityEvaluate;
        typedef AuthoredDepthSurface SurfaceEvaluate;
        #define CLUSTER_MESHLET_RASTER 1
        #define CLUSTER_SURFACE_RECEIVERS 1
        #define CLUSTER_SOFTWARE_MESHLET_RASTER 1
        #include "cluster_material_entries.slang"
        """;
}
