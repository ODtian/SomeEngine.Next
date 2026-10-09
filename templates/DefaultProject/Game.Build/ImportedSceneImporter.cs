using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Game.Assets;
using ImportedScenes;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace Game.Build;

[AssetImporter("a91c86d1-551d-48e3-8e33-a25e30e2c2f4", typeof(WorldBlock), SettingsProvider = typeof(GltfSceneSettingsProvider))]
public sealed class ImportedSceneImporter(GltfImporterSettings sourceSettings) : GltfSceneImporter
{
    public static readonly Guid ImporterId = new("a91c86d1-551d-48e3-8e33-a25e30e2c2f4");
    private readonly Dictionary<(float Cutoff, bool TwoSided), Guid> _shaders = [];
    private AssetId<TextureAsset> _white;
    public override async ValueTask ImportMaterialAsync(GltfMaterial source, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        var textures = new Dictionary<GltfTexture, AssetId<TextureAsset>>();
        foreach (GltfTexture texture in source.Channels
            .Where(channel => channel.Key is "BaseColor" or "Normal" or "MetallicRoughness" or "Occlusion" or "Emissive")
            .Select(channel => channel.Texture).OfType<GltfTexture>())
        {
            if (textures.ContainsKey(texture)) continue;
            TextureAsset imported = await context.GetAssetAsync<GltfTexture, TextureAsset>(
                texture, cancellationToken).ConfigureAwait(false);
            textures.Add(texture, imported.Reference);
        }
        string templatePath = Path.IsPathRooted(sourceSettings.LitMaterialTemplate)
            ? Path.GetFullPath(sourceSettings.LitMaterialTemplate)
            : Path.GetFullPath(Path.Combine(context.ProjectRoot, sourceSettings.LitMaterialTemplate));
        await context.ReportInputAsync(templatePath, cancellationToken).ConfigureAwait(false);
        await using MaterialAsset template = await MaterialAsset.OpenAsync(templatePath, cancellationToken).ConfigureAwait(false);
        using MaterialAsset asset = template.CreateCopy();
        var content = asset.GetBlock<MaterialBlock>();
        var values = new StandardMaterialParameters(content);
        var color = source.FindChannel("BaseColor");
        var surface = source.FindChannel("MetallicRoughness");
        var emissive = source.FindChannel("Emissive");
        values.BaseColorTint = color?.Color ?? Vector4.One;
        values.MetallicFactor = surface?.GetFactor("MetallicFactor") ?? 1;
        values.Roughness = surface?.GetFactor("RoughnessFactor") ?? 1;
        Vector4 emission = emissive?.Color ?? Vector4.Zero;
        float strength = emissive?.GetFactor("EmissiveStrength") ?? 1;
        values.EmissiveFactor = new Vector3(emission.X, emission.Y, emission.Z) * strength;
        values.AlbedoMap = color?.Texture is { } albedo && textures.TryGetValue(albedo, out AssetId<TextureAsset> albedoId)
            ? albedoId : _white;
        values.ARMMap = surface?.Texture is { } arm && textures.TryGetValue(arm, out AssetId<TextureAsset> armId)
            ? armId : _white;
        if (source.FindChannel("Normal")?.Texture is { } normal
            && textures.TryGetValue(normal, out AssetId<TextureAsset> normalId)) values.NormalMap = normalId;
        AssetId<TextureAsset> emissiveId = emissive?.Texture is { } emissiveTexture
            && textures.TryGetValue(emissiveTexture, out AssetId<TextureAsset> mappedEmissive) ? mappedEmissive : _white;
        content.SetParameter(new("EmissiveMap", MaterialParameterKind.Texture) { Texture = emissiveId });
        var occlusion = source.FindChannel("Occlusion");
        AssetId<TextureAsset> occlusionId = occlusion?.Texture is { } occlusionTexture
            && textures.TryGetValue(occlusionTexture, out AssetId<TextureAsset> mappedOcclusion) ? mappedOcclusion : _white;
        content.SetParameter(new("OcclusionMap", MaterialParameterKind.Texture) { Texture = occlusionId });
        content.SetParameter(new("NormalScale", MaterialParameterKind.Float) { Float = source.FindChannel("Normal")?.GetFactor("NormalScale") ?? 1 });
        content.SetParameter(new("OcclusionStrength", MaterialParameterKind.Float) { Float = occlusion?.GetFactor("OcclusionStrength") ?? 1 });
        content.Shader = new(_shaders[(Cutoff(source), source.DoubleSided)]);
        MaterialBlock block = content.Duplicate();
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
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
        WriteSurfaceShader(directory, defaults);
        foreach (var (cutoff, twoSided) in model.LogicalMaterials.Select(material => (Cutoff(material), material.DoubleSided)).Distinct())
        {
            string suffix = cutoff < 0 ? "opaque" : "mask_" + BitConverter.SingleToInt32Bits(cutoff).ToString("x8");
            string shaderName = "scene_" + suffix + (twoSided ? "_both" : "");
            string shader = File.ReadAllText(Path.Combine(defaults, "standard.slang"))
                .Replace("\"standard_material.slang\"", "\"scene_material.slang\"");
            if (twoSided) shader = "#define CLUSTER_RASTER_TWO_SIDED 1\n" + shader;
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
            definition["Blocks"]![0]!["Fields"]!["SourceNames"] = new JsonArray(shaderName + ".slang");
            WriteIfChanged(definitionPath, definition.ToJsonString());
            await project.BuildAsync(definitionPath);
            _shaders.Add((cutoff, twoSided), shaderId);
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
            using var replacement = new SceneMaterialAsset(template.Id) { Name = "Imported scene material" };
            var block = replacement.GetBlock<MaterialBlock>();
            foreach (var parameter in old.Parameters) block.SetParameter(parameter);
            block.Shader = new(_shaders.Values.First());
            project.RegisterAsset(await replacement.SaveAsync(templatePath));
        }
    }

    internal static void WriteSurfaceShader(string directory, string defaults)
    {
        string surface = File.ReadAllText(Path.Combine(defaults, "standard_material.slang"));
        surface = Replace(surface, "float3 EmissiveFactor;", "float3 EmissiveFactor;\n    float NormalScale;\n    float OcclusionStrength;");
        surface = Replace(surface, "Texture2D ARMMap;", "Texture2D ARMMap;\n    Texture2D OcclusionMap;\n    Texture2D EmissiveMap;");
        surface = Replace(surface, "return NormalMap.SampleLevel(MaterialSampler, uv, 0.0).rgb * 2.0 - 1.0;",
            "float3 normal = NormalMap.SampleLevel(MaterialSampler, uv, 0.0).rgb * 2.0 - 1.0;\n" +
            "        normal.xy *= LoadMaterialScalars<StandardScalars>(materialIndex).NormalScale;\n" +
            "        return normal;");
        surface = Replace(surface, "data.BaseColor = albedo.rgb", "data.BaseColor = SceneSrgbToLinear(albedo.rgb)");
        surface = Replace(surface, "data.Occlusion = arm.r;",
            "data.Occlusion = lerp(1.0, OcclusionMap.SampleLevel(MaterialSampler, surface.TexCoord, 0.0).r, scalars.OcclusionStrength);");
        surface = Replace(surface, "data.Emissive = scalars.EmissiveFactor;",
            "data.Emissive = SceneSrgbToLinear(EmissiveMap.SampleLevel(MaterialSampler, surface.TexCoord, 0.0).rgb) * scalars.EmissiveFactor;");
        surface = "float3 SceneSrgbToLinear(float3 color) { return select(color <= 0.04045, color / 12.92, pow((color + 0.055) / 1.055, 2.4)); }\n" + surface;
        WriteIfChanged(Path.Combine(directory, "scene_material.slang"), surface);
    }

    protected override ValueTask AddSceneContentAsync(World world, Scene scene, ImportContext context,
        CancellationToken cancellationToken)
    {
        string name = Path.GetFileNameWithoutExtension(context.SourcePath);
        bool sponza = name.Equals("Sponza", StringComparison.OrdinalIgnoreCase);
        world.CreateEntity(new SceneCamera
        {
            Position = sponza ? new(-9, 2, 0) : new(-8, 3, 0),
            Target = sponza ? new(5, 2, 0) : new(0, 2, 0),
            Up = Vector3.UnitY, VerticalFieldOfView = MathF.PI / 3, NearPlane = 0.05f, FarPlane = 2000,
        });
        world.CreateEntity(new DirectionalLight(new(0.35f, -0.85f, 0.4f), new(1, 0.96f, 0.9f), 3));
        if (sponza) world.CreateEntity(new PointLight(new(0, 4, 0), 30, new(1, 0.95f, 0.9f), 15));
        return ValueTask.CompletedTask;
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
[VisibilityDepth("masked")]
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
        result.Depth = input.Depth;
        if (input.TargetPolicy != 0u && input.EncodedTargetDepth >= asuint(1.0 - saturate(input.Depth)))
        {
            result.Visible = 0u;
            return result;
        }
        result.Visible = AlbedoMap.SampleLevel(MaterialSampler, varying.UV, 0.0).a >= ALPHA_CUTOFF ? 1u : 0u;
        return result;
    }
};
""";
}

[AssetCapabilities]
public static partial class ImportedSceneCapabilities;

// This project's material shader consumes UV0; the source file can retain other UV sets.
internal sealed class SceneVertexEncoder : IClusterVertexEncoder
{
    public string Compatibility => StandardClusterVertexEncoder.EncodingCompatibility;
    public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
        IReadOnlyList<RawAttribute> attributes, ReadOnlySpan<uint> clusterVertices)
        => StandardClusterVertexEncoder.Instance.Encode(positions,
            attributes.Where(value => !value.Name.StartsWith("TEXCOORD_") || value.Name == "TEXCOORD_0").ToArray(), clusterVertices);
}
