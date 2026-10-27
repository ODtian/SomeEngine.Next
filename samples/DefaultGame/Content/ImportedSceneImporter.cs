using SomeEngine.Geometry.Cluster;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.Assets.Schema;
using ImportedScenes;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.ECS;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace DefaultGame.Content;

[AssetImporter("a91c86d1-551d-48e3-8e33-a25e30e2c2f4", typeof(WorldBlock), SettingsProvider = typeof(GltfSceneSettingsProvider))]
public sealed class ImportedSceneImporter : GltfSceneImporter
{
    public static readonly Guid ImporterId = new("a91c86d1-551d-48e3-8e33-a25e30e2c2f4");
    public override async ValueTask ImportMaterialAsync(GltfMaterial source, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        if (source.Unlit)
        {
            await new StandardGltfSceneImporter().ImportMaterialAsync(source, context, output, cancellationToken);
            return;
        }
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
        string templatePath = Path.Combine(context.ProjectRoot, SampleSceneMaterials.ImportedTemplatePath);
        await context.ReportInputAsync(templatePath, cancellationToken).ConfigureAwait(false);
        await using MaterialAsset template = await MaterialAsset.OpenAsync(templatePath, cancellationToken).ConfigureAwait(false);
        using var asset = new MaterialAsset();
        var content = asset.GetBlock<MaterialBlock>();
        content.Parent = template.Reference;
        WriteMaterial(source, content, textures);
        string shaderPath = Path.Combine(context.ProjectRoot, "assets/Shaders", ShaderName(Cutoff(source)) + ".shader");
        await context.ReportInputAsync(shaderPath, cancellationToken);
        using ShaderAsset shader = await ShaderAsset.OpenSourceAsync(shaderPath, cancellationToken);
        content.Shader = shader.Reference;
        MaterialBlock block = content.Duplicate();
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }

    private static void WriteMaterial(GltfMaterial source, MaterialBlock content,
        IReadOnlyDictionary<GltfTexture, AssetId<TextureAsset>> textures)
    {
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
            ? albedoId : MaterialTemplates.White;
        values.ARMMap = surface?.Texture is { } arm && textures.TryGetValue(arm, out AssetId<TextureAsset> armId)
            ? armId : MaterialTemplates.White;
        if (source.FindChannel("Normal")?.Texture is { } normal
            && textures.TryGetValue(normal, out AssetId<TextureAsset> normalId)) values.NormalMap = normalId;
        AssetId<TextureAsset> emissiveId = emissive?.Texture is { } emissiveTexture
            && textures.TryGetValue(emissiveTexture, out AssetId<TextureAsset> mappedEmissive) ? mappedEmissive : MaterialTemplates.White;
        content.SetParameter(new("EmissiveMap", MaterialParameterKind.Texture) { Texture = emissiveId });
        var occlusion = source.FindChannel("Occlusion");
        AssetId<TextureAsset> occlusionId = occlusion?.Texture is { } occlusionTexture
            && textures.TryGetValue(occlusionTexture, out AssetId<TextureAsset> mappedOcclusion) ? mappedOcclusion : MaterialTemplates.White;
        content.SetParameter(new("OcclusionMap", MaterialParameterKind.Texture) { Texture = occlusionId });
        content.SetParameter(new("NormalScale", MaterialParameterKind.Float) { Float = source.FindChannel("Normal")?.GetFactor("NormalScale") ?? 1 });
        content.SetParameter(new("OcclusionStrength", MaterialParameterKind.Float) { Float = occlusion?.GetFactor("OcclusionStrength") ?? 1 });
    }

    internal async Task UpdateMaterialsAsync(AssetProject project, ModelRoot model, AssetManifest imported)
    {
        var entries = imported.Assets.Values.ToDictionary(entry => entry.SubAssetKey);
        var textures = model.LogicalTextures.ToDictionary(texture => texture,
            texture => new AssetId<TextureAsset>(entries[$"texture:index:{texture.LogicalIndex}"].AssetGuid));
        foreach (GltfMaterial source in model.LogicalMaterials)
        {
            AssetEntry entry = entries[$"material:index:{source.LogicalIndex}"];
            using var asset = new MaterialAsset(entry.AssetGuid) { Name = entry.Name };
            MaterialBlock content = asset.GetBlock<MaterialBlock>();
            content.Parent = SampleSceneMaterials.ImportedTemplate;
            WriteMaterial(source, content, textures);
            using ShaderAsset shader = await ShaderAsset.OpenSourceAsync(Path.Combine(project.RootPath,
                "assets/Shaders", ShaderName(Cutoff(source)) + ".shader"));
            content.Shader = shader.Reference;
            string destination = Path.Combine(project.RootPath, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            project.RegisterAsset(await asset.SaveAsync(destination));
        }
    }

    public async Task BuildAsync(AssetProject project, ModelRoot model, string engine)
    {
        string directory = Path.Combine(project.RootPath, "assets", "Shaders");
        string defaults = Path.Combine(engine, "samples", "DefaultGame", "assets", "Shaders");
        foreach (string file in Directory.EnumerateFiles(defaults, "*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".slang") || x.EndsWith(".shader")))
        {
            string destination = Path.Combine(directory, Path.GetRelativePath(defaults, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        string surface = File.ReadAllText(Path.Combine(engine, "assets", "Shaders", "standard_material.slang"));
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
        WriteIfChanged(Path.Combine(directory, "cluster_transparency.slang"),
            File.ReadAllText(Path.Combine(engine, "assets/Shaders/cluster_transparency.slang")));
        var shaderIds = new List<Guid>();
        foreach (float cutoff in model.LogicalMaterials.Where(static material => !material.Unlit).Select(Cutoff).Distinct())
        {
            string shaderName = ShaderName(cutoff);
            string shader = CreateSurfaceShader(defaults, shaderName, cutoff, twoSided: false);
            WriteIfChanged(Path.Combine(directory, shaderName + ".slang"), shader);
            WriteSurfaceEntries(directory, shaderName);
            string definitionPath = Path.Combine(directory, shaderName + ".shader");
            JsonNode definition = CreateSurfaceDefinition(definitionPath, defaults, shaderName, cutoff);
            Guid shaderId = Guid.Parse(definition["Fields"]!["Id"]!.GetValue<string>());
            WriteIfChanged(definitionPath, definition.ToJsonString());
            await project.BuildAsync(definitionPath);
            shaderIds.Add(shaderId);
        }
        string templatePath = Path.Combine(project.RootPath, "assets/Materials/Standard.material.asset");
        using var standard = await MaterialAsset.OpenAsync(templatePath);
        using var template = standard.CreateCopy(SampleSceneMaterials.ImportedTemplate.Value);
        var old = template.GetBlock<MaterialBlock>();
        old.Shader = new(shaderIds.First());
        template.Name = "Imported scene material";
        project.RegisterAsset(await template.SaveAsync(Path.Combine(project.RootPath, SampleSceneMaterials.ImportedTemplatePath)));
    }

    internal static void WriteSurfaceSources(string directory, string engineRoot)
    {
        WriteIfChanged(Path.Combine(directory, "cluster_transparency.slang"),
            File.ReadAllText(Path.Combine(engineRoot, "assets/Shaders/cluster_transparency.slang")));
        foreach (string definitionPath in Directory.EnumerateFiles(directory, "scene_*.shader"))
        {
            string shaderName = Path.GetFileNameWithoutExtension(definitionPath);
            string sourcePath = Path.Combine(directory, shaderName + ".slang");
            string suffix = shaderName["scene_".Length..];
            bool twoSided = suffix.EndsWith("_both", StringComparison.Ordinal);
            string surface = twoSided ? suffix[..^"_both".Length] : suffix;
            float cutoff = surface == "opaque" ? -1 : BitConverter.Int32BitsToSingle(
                int.Parse(surface["mask_".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            string source = CreateSurfaceShader(Path.Combine(engineRoot, "templates/DefaultProject/assets/Shaders"),
                shaderName, cutoff, twoSided);
            WriteIfChanged(sourcePath, source);
            WriteSurfaceEntries(directory, shaderName);
            JsonNode definition = CreateSurfaceDefinition(definitionPath,
                Path.Combine(engineRoot, "templates/DefaultProject/assets/Shaders"), shaderName, cutoff);
            WriteIfChanged(definitionPath, definition.ToJsonString());
        }
    }

    private static JsonNode CreateSurfaceDefinition(string path, string defaults, string name, float cutoff)
    {
        var definition = JsonNode.Parse(File.ReadAllText(Path.Combine(defaults, "standard.shader")))!;
        Guid id = File.Exists(path)
            ? Guid.Parse(JsonNode.Parse(File.ReadAllText(path))!["Fields"]!["Id"]!.GetValue<string>()) : Guid.NewGuid();
        definition["Fields"]!["Id"] = id.ToString();
        definition["Fields"]!["Name"] = name;
        definition["Blocks"]![0]!["Fields"]!["SourceNames"] = new JsonArray("cluster_transparency.slang", name + ".slang", name + "_transparency.slang");
        definition["Blocks"]![0]!["Fields"]!["Specializations"] =
            JsonSerializer.SerializeToNode(SampleContent.CreateMaterialSpecializations(cutoff >= 0 ? 1 : 0));
        return definition;
    }

    private static void WriteSurfaceEntries(string directory, string name) =>
        WriteIfChanged(Path.Combine(directory, name + "_transparency.slang"),
            "import \"" + name + "\";\n#define TRANSPARENCY_MATERIAL_TYPE SceneTransparencyMaterial_" + name +
            "\n#include \"cluster_transparency_entries.slang\"\n");

    private static string CreateSurfaceShader(string defaults, string shaderName, float cutoff, bool twoSided)
    {
        string source = File.ReadAllText(Path.Combine(defaults, "standard.slang"))
            .Replace("\"standard_material.slang\"", "\"scene_material.slang\"")
            .Replace("StandardTransparencyMaterial", "SceneTransparencyMaterial_" + shaderName);
        if (twoSided) source = "#define CLUSTER_RASTER_TWO_SIDED 1\n" + source;
        if (cutoff >= 0)
            source = Replace(source, "typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;",
                MaskShader.Replace("ALPHA_CUTOFF", cutoff.ToString("R", CultureInfo.InvariantCulture)) +
                "\ntypedef SceneMask VisibilityEvaluate;");
        return source;
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
    private static string ShaderName(float cutoff) => cutoff < 0 ? "scene_opaque"
        : "scene_mask_" + BitConverter.SingleToInt32Bits(cutoff).ToString("x8");
    private static string Replace(string source, string previous, string next) => source.Contains(previous)
        ? source.Replace(previous, next) : throw new InvalidDataException($"The sample shader integration changed: {previous}");
    private static void WriteIfChanged(string path, string content)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
    }

    private const string MaskShader = """
struct SceneMaskVarying { float2 UV : TEXCOORD0; };
[VisibilityDepth("masked")]
[MaterialDepthInputs("AlbedoMap")]
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

[AssetCapabilities(GltfImporterSets.ClusterId)]
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
