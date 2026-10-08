using System.Text;
using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

public enum RasterMaterialKind { Ordinary, Tessellated, AdditionalTessellated }

internal static class RasterTestShaders
{
    private static readonly Dictionary<(string Backend, RasterMaterialKind Kind), Shader> Shaders = [];

    internal static Shader Create(string backend, RasterMaterialKind kind)
    {
        if (Shaders.TryGetValue((backend, kind), out Shader? shader)) return shader;
        string root = FrameStageScene.FindRoot();
        string projectShaders = Path.Combine(root, "templates", "DefaultProject", "assets", "Shaders");
        string source = File.ReadAllText(Path.Combine(projectShaders, "unlit.slang"))
            .Replace("typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;",
                VisibilitySource + "\ntypedef TextureVisibility VisibilityEvaluate;");
        if (kind != RasterMaterialKind.Ordinary)
            source += "\n#include \"cluster_tessellation_entries.slang\"\n";
        SlangCompileTarget target = backend == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil;
        var description = TestShaderLoading.CreateSessionDescription(target);
        description.SearchPaths = [projectShaders, Path.Combine(root, "assets", "Shaders")];
        IGlobalSession slang = TestShaderLoading.Slang;
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        ISlangBlob text = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
        string name = $"RasterTest{kind}";
        IModule? unit = session.LoadModuleFromSource(name, name + ".slang", text, out ISlangBlob? diagnostics);
        Assert.True(unit is not null, diagnostics is null ? name : Encoding.UTF8.GetString(diagnostics.Buffer));
        var entries = new List<IEntryPoint>();
        for (int index = 0; index < unit!.GetDefinedEntryPointCount(); index++)
        {
            Assert.True(unit.GetDefinedEntryPoint(index, out IEntryPoint entry).Succeeded);
            entries.Add(entry);
        }
        Assert.True(session.CreateCompositeComponentType([unit, .. entries], out IComponentType combined,
            out diagnostics).Succeeded, DiagnosticText(diagnostics));
        Assert.True(combined.Link(out IComponentType linked, out diagnostics).Succeeded, DiagnosticText(diagnostics));
        shader = Shader.CreateFromSlang(name, slang, session, [unit], entries, linked,
            description.Targets);
        Shaders.Add((backend, kind), shader);
        return shader;
    }

    internal static Shader CreateWithUnresolvedShade(string backend)
    {
        string root = FrameStageScene.FindRoot();
        string projectShaders = Path.Combine(root, "templates", "DefaultProject", "assets", "Shaders");
        string material = File.ReadAllText(Path.Combine(projectShaders, "unlit_material.slang"))
            .Replace("struct UnlitMaterial :", "extern float3 missingShadeColor();\nstruct UnlitMaterial :")
            .Replace("albedoSample.rgb * scalars.BaseColorTint.rgb", "missingShadeColor()");
        string source = File.ReadAllText(Path.Combine(projectShaders, "unlit.slang"))
            .Replace("#include \"unlit_material.slang\"", material);
        var description = TestShaderLoading.CreateSessionDescription(
            backend == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil);
        description.SearchPaths = [projectShaders, Path.Combine(root, "assets", "Shaders")];
        var slang = TestShaderLoading.Slang;
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        var text = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
        var unit = session.LoadModuleFromSource("UnresolvedShade", "UnresolvedShade.slang", text, out var diagnostics);
        Assert.True(unit is not null, DiagnosticText(diagnostics));
        var entries = new List<IEntryPoint>();
        for (int index = 0; index < unit!.GetDefinedEntryPointCount(); index++)
        {
            Assert.True(unit.GetDefinedEntryPoint(index, out IEntryPoint entry).Succeeded);
            entries.Add(entry);
        }
        Assert.True(session.CreateCompositeComponentType([unit, .. entries], out IComponentType combined,
            out diagnostics).Succeeded, DiagnosticText(diagnostics));
        Assert.True(combined.Link(out var linked, out diagnostics).Succeeded, DiagnosticText(diagnostics));
        var reflection = linked.GetLayout(0, out diagnostics);
        int shade = Enumerable.Range(0, entries.Count).Single(index =>
            reflection.GetEntryPointByIndex((uint)index).Name == "ClusterShade");
        Assert.True(linked.GetEntryPointCode(shade, 0, out _, out diagnostics).Failed);
        Assert.Contains("missingShadeColor", DiagnosticText(diagnostics));
        return Shader.CreateFromSlang("Unresolved shade", slang, session, [unit], entries, combined,
            description.Targets, generateCode: false);
    }

    private static string DiagnosticText(ISlangBlob? text) => text is null ? "" : Encoding.UTF8.GetString(text.Buffer);

    // The visibility texture gives each material its own raster bin while tessellated materials
    // share the split shader and its resource bindings. Every test texture has opaque alpha.
    private const string VisibilitySource = """
        [VisibilityDepth("masked")]
        struct TextureVisibility : IVisibilityEvaluate<StandardVertex>
        {
            typealias Varying = EmptyVisibilityVarying;
            Texture2D<float4> AlbedoMap;
            SamplerState MaterialSampler;
            Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input) { return {}; }
            Varying readLane(Varying value, uint lane) { return {}; }
            Varying interpolateVertex(Varying first, Varying second, Varying third, float3 bary) { return {}; }
            Varying interpolatePixel(Varying first, Varying second, Varying third, VisibilityInterpolationInput input) { return {}; }
            VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
            {
                VisibilityPixelResult result;
                result.Visible = AlbedoMap.SampleLevel(MaterialSampler, float2(0.5, 0.5), 0).a >= 0.5 ? 1u : 0u;
                result.Depth = input.Depth;
                return result;
            }
        }
        """;
}
