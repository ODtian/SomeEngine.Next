using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Game.Assets;
using ImportedScenes;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfTexture = SharpGLTF.Schema2.Texture;

sealed class SceneMaterials : IGltfMaterialConversion
{
    private readonly Dictionary<float, Guid> _shaders = [];
    private AssetId<TextureAsset> _white;
    public IEnumerable<GltfTexture> Textures(GltfMaterial material)
        => material.Channels.Where(x => x.Key is "BaseColor" or "Normal" or "MetallicRoughness" or "Occlusion" or "Emissive")
            .Select(x => x.Texture).OfType<GltfTexture>();

    public void Apply(MaterialAsset asset, GltfMaterial material, Func<GltfTexture?, AssetId<TextureAsset>> resolveTexture)
    {
        var content = asset.GetBlock<MaterialBlock>();
        var values = new StandardMaterialParameters(content);
        var color = material.FindChannel("BaseColor");
        var surface = material.FindChannel("MetallicRoughness");
        var emissive = material.FindChannel("Emissive");
        values.BaseColorTint = color?.Color ?? Vector4.One;
        values.MetallicFactor = surface?.GetFactor("MetallicFactor") ?? 1;
        values.Roughness = surface?.GetFactor("RoughnessFactor") ?? 1;
        Vector4 emission = emissive?.Color ?? Vector4.Zero;
        float strength = emissive?.GetFactor("EmissiveStrength") ?? 1;
        values.EmissiveFactor = new Vector3(emission.X, emission.Y, emission.Z) * strength;
        values.AlbedoMap = TextureOrWhite(color?.Texture);
        values.ARMMap = TextureOrWhite(surface?.Texture);
        if (material.FindChannel("Normal")?.Texture is { } normal) values.NormalMap = resolveTexture(normal);
        content.SetParameter(new("EmissiveMap", MaterialParameterKind.Texture) { Texture = TextureOrWhite(emissive?.Texture) });
        var occlusion = material.FindChannel("Occlusion");
        content.SetParameter(new("OcclusionMap", MaterialParameterKind.Texture) { Texture = TextureOrWhite(occlusion?.Texture) });
        content.SetParameter(new("NormalScale", MaterialParameterKind.Float) { Float = material.FindChannel("Normal")?.GetFactor("NormalScale") ?? 1 });
        content.SetParameter(new("OcclusionStrength", MaterialParameterKind.Float) { Float = occlusion?.GetFactor("OcclusionStrength") ?? 1 });
        content.Shader = new(_shaders[Cutoff(material)]);
        AssetId<TextureAsset> TextureOrWhite(GltfTexture? texture) => texture is null ? _white : resolveTexture(texture);
    }

    public async Task BuildAsync(AssetProject project, ModelRoot model, string engine)
    {
        string directory = Path.Combine(project.RootPath, "assets", "Shaders");
        string defaults = Path.Combine(engine, "templates", "DefaultProject", "assets", "Shaders");
        foreach (string file in Directory.EnumerateFiles(defaults, "*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".slang") || x.EndsWith(".shader")))
        {
            string destination = Path.Combine(directory, Path.GetRelativePath(defaults, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        string surface = File.ReadAllText(Path.Combine(defaults, "standard_material.slang"));
        surface = Replace(surface, "float3 EmissiveFactor;", "float3 EmissiveFactor;\n    float NormalScale;\n    float OcclusionStrength;");
        surface = Replace(surface, "Texture2D ARMMap;", "Texture2D ARMMap;\n    Texture2D OcclusionMap;\n    Texture2D EmissiveMap;");
        surface = Replace(surface, "float3 N_geom =", "normalTS.xy *= scalars.NormalScale;\n        float3 N_geom =");
        surface = Replace(surface, "float3 baseColor = albedoSample.rgb", "float3 baseColor = SceneSrgbToLinear(albedoSample.rgb)");
        surface = Replace(surface, "float3 finalColor = baseColor * armSample.r * 0.03 + scalars.EmissiveFactor;",
            "float ao = lerp(1.0, OcclusionMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).r, scalars.OcclusionStrength);\n" +
            "        float3 emission = SceneSrgbToLinear(EmissiveMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb) * scalars.EmissiveFactor;\n" +
            "        float3 finalColor = baseColor * ao * 0.03 + emission;");
        surface = "float3 SceneSrgbToLinear(float3 color) { return select(color <= 0.04045, color / 12.92, pow((color + 0.055) / 1.055, 2.4)); }\n" + surface;
        WriteIfChanged(Path.Combine(directory, "scene_material.slang"), surface);
        foreach (float cutoff in model.LogicalMaterials.Select(Cutoff).Distinct())
        {
            string suffix = cutoff < 0 ? "opaque" : "mask_" + BitConverter.SingleToInt32Bits(cutoff).ToString("x8");
            string shaderName = "scene_" + suffix;
            string shader = File.ReadAllText(Path.Combine(defaults, "standard.slang"))
                .Replace("\"standard_material.slang\"", "\"scene_material.slang\"");
            if (cutoff >= 0)
                shader = Replace(shader, "typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;",
                    MaskShader.Replace("ALPHA_CUTOFF", cutoff.ToString("R", CultureInfo.InvariantCulture)) + "\ntypedef SceneMask VisibilityEvaluate;");
            WriteIfChanged(Path.Combine(directory, shaderName + ".slang"), shader);
            string definitionPath = Path.Combine(directory, shaderName + ".shader");
            var definition = JsonNode.Parse(File.ReadAllText(Path.Combine(defaults, "standard.shader")))!;
            Guid shaderId = File.Exists(definitionPath)
                ? Guid.Parse(JsonNode.Parse(File.ReadAllText(definitionPath))!["Fields"]!["Id"]!.GetValue<string>()) : Guid.NewGuid();
            definition["Fields"]!["Id"] = shaderId.ToString();
            definition["Fields"]!["Name"] = shaderName;
            definition["Blocks"]![0]!["Fields"]!["SourceFiles"] = new JsonArray(shaderName + ".slang");
            WriteIfChanged(definitionPath, definition.ToJsonString());
            await project.BuildAsync(definitionPath);
            _shaders.Add(cutoff, shaderId);
        }
        string templatePath = Path.Combine(project.RootPath, "assets/Materials/Standard.material.asset");
        using var template = await MaterialAsset.OpenAsync(templatePath);
        var old = template.GetBlock<MaterialBlock>();
        var previous = new StandardMaterialParameters(old);
        _white = previous.AlbedoMap;
        // The template is a project asset. Its concrete type is registered by the
        // sample viewer; the engine still sees an ordinary user material.
        if (old.MaterialTypeId != SceneMaterial.TypeId)
        {
            using var replacement = new SceneMaterialAsset(PageArena.Shared, template.Id) { Name = "Imported scene material" };
            var block = replacement.GetBlock<MaterialBlock>();
            foreach (var parameter in old.Parameters) block.SetParameter(parameter);
            block.Shader = new(_shaders.Values.First());
            project.RegisterAsset(await replacement.SaveAsync(templatePath));
        }
    }

    private static float Cutoff(GltfMaterial material) => material.Alpha == AlphaMode.MASK
        ? material.AlphaCutoff / Math.Max(material.FindChannel("BaseColor")?.Color.W ?? 1, 1e-8f) : -1;
    private static string Replace(string source, string previous, string next) => source.Contains(previous)
        ? source.Replace(previous, next) : throw new InvalidDataException($"The sample shader integration changed: {previous}");
    private static void WriteIfChanged(string path, string content)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
    }

    private const string MaskShader = """
struct SceneMaskVarying { float2 UV : TEXCOORD0; };
struct SceneMask : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = SceneMaskVarying;
    Texture2D<float4> AlbedoMap;
    SamplerState MaterialSampler;
    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input)
    { Varying result; result.UV = vertex.uv0; return result; }
    Varying readLane(Varying value, uint lane)
    { Varying result; result.UV = WaveReadLaneAt(value.UV, lane); return result; }
    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
    { Varying result; result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z; return result; }
    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
    { return interpolateVertex(a, b, c, input.PerspectiveBarycentric); }
    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Visible = AlbedoMap.SampleLevel(MaterialSampler, varying.UV, 0.0).a >= ALPHA_CUTOFF ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};
""";
}
