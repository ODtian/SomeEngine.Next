using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class MaterialTextureViewRenderingTests
{
    [Fact]
    public async Task OneTexturePropertyKeepsRasterStorageAndShadeSampledViewsAfterDiscardedReplacement()
    {
        Shader shader = CreateStorageVisibilityShader();
        // Hardware-only visibility selects one of these pixel entries for vertex or mesh rasterization.
        foreach (string entry in new[] { "ClusterRasterPixelHardwareOnly", "ClusterRasterMeshPixelHardwareOnly" })
        {
            ShaderParameterResource[] raster = shader.GetSlangResourceLayout(SlangCompileTarget.Dxil,
                entry).Resources.ToArray();
            Assert.Contains(raster, resource => resource.IsUsed && resource.Name == "AlbedoMap" &&
                resource.Type == ResourceBindingType.TextureUav);
            Assert.DoesNotContain(raster, resource => resource.IsUsed && resource.Name == "AlbedoMap" &&
                resource.Type == ResourceBindingType.TextureSrv);
        }
        ShaderParameterResource[] surface = shader.GetSlangResourceLayout(SlangCompileTarget.Dxil,
            "ClusterSurfaceData").Resources.ToArray();
        // Unlit samples AlbedoMap by its scalar descriptor index. The Cluster graph declares
        // that texture read through the same material input lookup used by explicit resources.
        Assert.Contains(surface, resource => resource.IsUsed && resource.Name == "MaterialScalarRegion" &&
            resource.Type == ResourceBindingType.BufferSrv);
        Assert.DoesNotContain(surface, resource => resource.IsUsed && resource.Name == "AlbedoMap" &&
            resource.Type == ResourceBindingType.TextureUav);

        using Texture blue = CreateTexture(0, 0, 255);
        using Texture red = CreateTexture(255, 0, 0);
        using Texture hidden = CreateTexture(255, 0, 0, 0);
        using var first = new Game.Content.UnlitMaterial
            { Shader = shader, AlbedoMap = blue, BaseColorTint = Vector4.One };
        using var second = new Game.Content.UnlitMaterial
            { Shader = shader, AlbedoMap = blue, BaseColorTint = Vector4.One };
        Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions
            { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, plane);
        scene.SetMeshTransform(0, new(new Vector3(-.8f, 0, -3), Quaternion.Identity)
            { Stretch = new(.6f, .6f, 1) });
        scene.SetMeshTransform(1, new(new Vector3(.8f, 0, -3), Quaternion.Identity)
            { Stretch = new(.6f, .6f, 1) });
        scene.UseMaterial(0, first);
        scene.UseMaterial(1, second);
        RasterImage before = await scene.ReadRasterImageAsync();
        AssertPixel(before, 16, 32, new(0, 0, 1, 1));
        AssertPixel(before, 48, 32, new(0, 0, 1, 1));

        first.AlbedoMap = red;
        await scene.DiscardNextFrameAsync();
        RasterImage discarded = scene.ReadCurrentRasterImage();
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, before.Color),
            new(64, 64, Format.R16G16B16A16Float, discarded.Color));
        RasterImage recovered = await scene.ReadNextRasterImageAsync();
        AssertPixel(recovered, 16, 32, new(1, 0, 0, 1));
        AssertPixel(recovered, 48, 32, new(0, 0, 1, 1));
        RasterImage unchanged = await scene.ReadNextRasterImageAsync();
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, recovered.Color),
            new(64, 64, Format.R16G16B16A16Float, unchanged.Color));

        first.AlbedoMap = hidden;
        RasterImage masked = await scene.ReadNextRasterImageAsync();
        int leftPixel = 32 * 64 + 16;
        ImageArtifacts.AssertEqual(new(1, 1, Format.R16G16B16A16Float, before.Color.AsSpan(0, 8).ToArray()),
            new(1, 1, Format.R16G16B16A16Float, masked.Color.AsSpan(leftPixel * 8, 8).ToArray()));
        ImageArtifacts.AssertEqual(new(1, 1, Format.D32Float, before.Depth.AsSpan(0, 4).ToArray()),
            new(1, 1, Format.D32Float, masked.Depth.AsSpan(leftPixel * 4, 4).ToArray()));
        AssertPixel(masked, 48, 32, new(0, 0, 1, 1));
    }

    private static Texture CreateTexture(byte red, byte green, byte blue, byte alpha = 255) => Texture.Create(
        new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopyDestination),
        [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 },
            new byte[] { red, green, blue, alpha })], TextureViewDimension.Texture2D);

    private static void AssertPixel(RasterImage image, int x, int y, Vector4 expected)
    {
        Half[] channels = [(Half)expected.X, (Half)expected.Y, (Half)expected.Z, (Half)expected.W];
        ImageArtifacts.AssertEqual(new(1, 1, Format.R16G16B16A16Float,
            MemoryMarshal.AsBytes(channels.AsSpan()).ToArray()),
            new(1, 1, Format.R16G16B16A16Float, image.Color.AsSpan((y * 64 + x) * 8, 8).ToArray()));
    }

    private static Shader CreateStorageVisibilityShader()
    {
        string root = FrameStageScene.FindRoot();
        string projectShaders = Path.Combine(root, "templates", "DefaultProject", "assets", "Shaders");
        string source = File.ReadAllText(Path.Combine(projectShaders, "unlit.slang"))
            .Replace("typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;",
                VisibilitySource + "\ntypedef StorageVisibility VisibilityEvaluate;");
        var description = TestShaderLoading.CreateSessionDescription(SlangCompileTarget.Dxil);
        description.SearchPaths = [projectShaders, Path.Combine(root, "assets", "Shaders")];
        IGlobalSession slang = TestShaderLoading.Slang;
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        ISlangBlob text = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
        IModule? unit = session.LoadModuleFromSource("MaterialTextureViews", "MaterialTextureViews.slang", text,
            out ISlangBlob? diagnostics);
        Assert.True(unit is not null, DiagnosticText(diagnostics));
        var entries = new List<IEntryPoint>();
        for (int index = 0; index < unit!.GetDefinedEntryPointCount(); index++)
        {
            Assert.True(unit.GetDefinedEntryPoint(index, out IEntryPoint entry).Succeeded);
            entries.Add(entry);
        }
        Assert.True(session.CreateCompositeComponentType([unit, .. entries], out IComponentType combined,
            out diagnostics).Succeeded, DiagnosticText(diagnostics));
        Assert.True(combined.Link(out IComponentType linked, out diagnostics).Succeeded, DiagnosticText(diagnostics));
        return Shader.CreateFromSlang("Material texture views", slang, session, [unit], entries, linked,
            description.Targets);
    }

    private static string DiagnosticText(ISlangBlob? text) => text is null ? "" : Encoding.UTF8.GetString(text.Buffer);

    private const string VisibilitySource = """
        [VisibilityDepth("masked")]
        struct StorageVisibility : IVisibilityEvaluate<StandardVertex>
        {
            typealias Varying = EmptyVisibilityVarying;
            RWTexture2D<float4> AlbedoMap;
            Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input) { return {}; }
            Varying readLane(Varying value, uint lane) { return {}; }
            Varying interpolateVertex(Varying first, Varying second, Varying third, float3 bary) { return {}; }
            Varying interpolatePixel(Varying first, Varying second, Varying third, VisibilityInterpolationInput input) { return {}; }
            VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
            {
                VisibilityPixelResult result;
                result.Visible = AlbedoMap[uint2(0, 0)].a >= 0.5 ? 1u : 0u;
                result.Depth = input.Depth;
                return result;
            }
        }
        """;
}
