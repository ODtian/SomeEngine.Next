using SomeEngine.Geometry.Cluster;
using System.Globalization;
using System.Numerics;
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

[AssetImporter("a91c86d1-551d-48e3-8e33-a25e30e2c2f4", typeof(WorldBlock), typeof(MaterialBlock), typeof(MeshMaterialsBlock),
    SettingsProvider = typeof(GltfSceneSettingsProvider),
    VersionInputs = new[] { typeof(StandardMaterialParameters), typeof(UnlitMaterialParameters), typeof(StandardGltfSceneImporter), typeof(StandardMaterialInputs) })]
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
        string templatePath = Path.Combine(context.ProjectRoot, SampleSceneMaterials.ImportedTemplatePath);
        await context.ReportInputAsync(templatePath, cancellationToken).ConfigureAwait(false);
        await using MaterialAsset template = await MaterialAsset.OpenAsync(templatePath, cancellationToken).ConfigureAwait(false);
        using var asset = new MaterialAsset();
        var content = asset.GetBlock<MaterialBlock>();
        content.Parent = template.Reference;
        // Retain this project's white fallback; generic callers can inherit their own template textures.
        _ = new StandardMaterialParameters(content) { AlbedoMap = MaterialTemplates.White, ARMMap = MaterialTemplates.White };
        await StandardGltfSceneImporter.ImportStandardParametersAsync(source, context, content, cancellationToken).ConfigureAwait(false);
        string shaderPath = Path.Combine(context.ProjectRoot, "assets/Shaders", ShaderName(Cutoff(source)) + ".shader");
        await context.ReportInputAsync(shaderPath, cancellationToken);
        using ShaderAsset shader = await ShaderAsset.OpenSourceAsync(shaderPath, cancellationToken);
        content.Shader = shader.Reference;
        MaterialBlock block = content.Duplicate();
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }

    public async Task BuildAsync(AssetProject project, ModelRoot model, string engine)
    {
        string directory = Path.Combine(project.RootPath, "assets", "Shaders");
        string defaults = Path.Combine(engine, "samples", "DefaultGame", "assets", "Shaders");
        foreach (string file in Directory.EnumerateFiles(defaults, "*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".slang") || x.EndsWith(".shader")))
        {
            string relativePath = Path.GetRelativePath(defaults, file);
            if (relativePath == "cluster_transparency.slang") continue;
            string destination = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        SampleContent.LinkTransparencySource(directory, Path.Combine(engine, "assets", "Shaders"));
        WriteSurfaceShader(directory, engine);
        var shaderIds = new List<Guid>();
        foreach (float cutoff in model.LogicalMaterials.Where(static material => !material.Unlit).Select(Cutoff).Distinct())
        {
            string shaderName = ShaderName(cutoff);
            WriteIfChanged(Path.Combine(directory, shaderName + ".slang"),
                CreateSurfaceShader(defaults, shaderName, cutoff, false));
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

    internal static void WriteSurfaceShader(string directory, string engineRoot)
    {
        // Ordinary PBR behavior lives in the engine shader; only this project's module name is local.
        WriteIfChanged(Path.Combine(directory, "scene_material.slang"),
            File.ReadAllText(Path.Combine(engineRoot, "assets", "Shaders", "standard_material.slang")));
    }

    internal static void WriteSurfaceSources(string directory, string engineRoot)
    {
        SampleContent.LinkTransparencySource(directory, Path.Combine(engineRoot, "assets", "Shaders"));
        foreach (string definitionPath in Directory.EnumerateFiles(directory, "scene_*.shader"))
        {
            string shaderName = Path.GetFileNameWithoutExtension(definitionPath);
            string sourcePath = Path.Combine(directory, shaderName + ".slang");
            string suffix = shaderName["scene_".Length..];
            bool twoSided = suffix.EndsWith("_both", StringComparison.Ordinal);
            string surface = twoSided ? suffix[..^"_both".Length] : suffix;
            float cutoff = surface == "opaque" ? -1 : BitConverter.Int32BitsToSingle(
                int.Parse(surface["mask_".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            string source = CreateSurfaceShader(Path.Combine(engineRoot, "samples/DefaultGame/assets/Shaders"),
                shaderName, cutoff, twoSided);
            WriteIfChanged(sourcePath, source);
            WriteSurfaceEntries(directory, shaderName);
            JsonNode definition = CreateSurfaceDefinition(definitionPath,
                Path.Combine(engineRoot, "samples/DefaultGame/assets/Shaders"), shaderName, cutoff);
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
        var specializations = new JsonArray();
        string depth = cutoff >= 0 ? "1" : "0";
        foreach (string mode in new[] { "0", "8" })
        {
            specializations.Add(new JsonObject { ["EntryPoint"] = "ClusterShadeMode", ["Arguments"] = new JsonArray(mode) });
            specializations.Add(new JsonObject { ["EntryPoint"] = "ClusterShadowRequests", ["Arguments"] = new JsonArray(depth, mode) });
            specializations.Add(new JsonObject { ["EntryPoint"] = "ClusterForwardShade", ["Arguments"] = new JsonArray(depth, mode) });
        }
        foreach (string entry in new[] { "BuildTransparentGeometry", "RequestTransparentPages", "ResolveTransparentColor", "ResolveProjectedTransparentColor" })
            foreach (string tessellated in new[] { "false", "true" })
                specializations.Add(new JsonObject { ["EntryPoint"] = entry, ["Arguments"] = new JsonArray(depth, tessellated) });
        definition["Blocks"]![0]!["Fields"]!["Specializations"] = specializations;
        return definition;
    }

    private static void WriteSurfaceEntries(string directory, string name) =>
        WriteIfChanged(Path.Combine(directory, name + "_transparency.slang"),
            "import \"" + name + "\";\n#define TRANSPARENCY_MATERIAL_TYPE SceneTransparencyMaterial_" + name +
            "\n#include \"cluster_transparency_entries.slang\"\n");

    private static string CreateSurfaceShader(string defaults, string shaderName, float cutoff, bool twoSided, bool trackDepthInputs = false)
    {
        string source = File.ReadAllText(Path.Combine(defaults, "standard.slang"))
            .Replace("\"standard_material.slang\"", "\"scene_material.slang\"")
            .Replace("StandardTransparencyMaterial", "SceneTransparencyMaterial_" + shaderName);
        if (twoSided) source = "#define CLUSTER_RASTER_TWO_SIDED 1\n" + source;
        if (cutoff >= 0)
            source = Replace(source, "typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;",
                StandardMaterialInputs.CreateAlphaMaskSource(cutoff, trackDepthInputs) +
                "\ntypedef StandardAlphaMask VisibilityEvaluate;");
        return source;
    }

    /// <summary>Rebuilds one selected scene shader without moving compilation out of ShaderAsset.</summary>
    public static async Task<Guid> BuildMaterialShaderAsync(
        AssetProject project, string sourceDefinitionPath, string engineRoot, bool regenerateMaterial = false,
        bool identityTies = false, string? outputPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        sourceDefinitionPath = Path.GetFullPath(sourceDefinitionPath);
        engineRoot = Path.GetFullPath(engineRoot);
        string directory = Path.Combine(project.RootPath, "assets", "Shaders");
        string defaults = Path.Combine(engineRoot, "samples", "DefaultGame", "assets", "Shaders");
        if (!Directory.Exists(directory) || !Directory.Exists(defaults))
            throw new DirectoryNotFoundException("Single-material cook reuses existing shader directories only.");
        JsonObject definition = JsonNode.Parse(File.ReadAllText(sourceDefinitionPath)) as JsonObject
            ?? throw new InvalidDataException("The existing shader definition is not an object.");
        string name = definition["Fields"]?["Name"]?.GetValue<string>()
            ?? throw new InvalidDataException("The existing shader has no name.");
        if (definition["Fields"]?["AssetType"]?.GetValue<string>() != ShaderAsset.FileType ||
            !Guid.TryParse(definition["Fields"]?["Id"]?.GetValue<string>(), out Guid shaderId) || shaderId == Guid.Empty)
            throw new InvalidDataException("The existing shader identity is invalid.");
        bool twoSided = name.EndsWith("_both", StringComparison.Ordinal);
        string variant = twoSided ? name[..^5] : name;
        float cutoff;
        if (variant == "scene_opaque") cutoff = -1;
        else if (variant.StartsWith("scene_mask_", StringComparison.Ordinal) && variant.Length == 19 &&
            uint.TryParse(variant.AsSpan(11), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
        {
            cutoff = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            if (!float.IsFinite(cutoff) || cutoff < 0)
                throw new InvalidDataException("The existing masked variant has an invalid cutoff.");
        }
        else throw new InvalidDataException("Only existing scene_opaque or scene_mask_<floatbits> variants are supported.");
        string sourceStem = Path.GetFileNameWithoutExtension(sourceDefinitionPath);
        if (sourceStem != name && sourceStem != name + "_" + shaderId.ToString("N"))
            throw new InvalidDataException("The existing shader definition name does not match its identity.");
        JsonArray sourceNames = definition["Blocks"]?.AsArray()
            .Select(block => block?["Fields"]?["SourceNames"]).OfType<JsonArray>().SingleOrDefault()
            ?? throw new InvalidDataException("The existing shader has no unique source definition.");
        int materialSource = -1;
        int transparencySource = -1;
        for (int index = 0; index < sourceNames.Count; index++)
        {
            string? input = sourceNames[index]?.GetValue<string>();
            if (input == sourceStem + ".slang") materialSource = index;
            else if (input == sourceStem + "_transparency.slang") transparencySource = index;
            else if (input != "cluster_transparency.slang")
                throw new InvalidDataException("The existing shader contains an unsupported scene source.");
        }
        if (materialSource < 0 || sourceNames.Count != 1 && (sourceNames.Count != 3 || transparencySource < 0))
            throw new InvalidDataException("The existing material sources do not match its identity.");
        string localName = name + "_" + shaderId.ToString("N");
        string destination = Path.Combine(directory, localName + ".shader");
        string? outputDestination = outputPath is null ? null : Path.GetFullPath(outputPath, project.RootPath);
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (project.Manifest.TryGetAsset(shaderId, out AssetEntry registered))
        {
            if (registered.AssetType != ShaderAsset.FileType || registered.DefinitionPath.Length == 0)
                throw new InvalidDataException("The registered shader has no compatible existing definition.");
            destination = Path.GetFullPath(registered.DefinitionPath, project.RootPath);
            string registeredOutput = Path.GetFullPath(registered.Path, project.RootPath);
            if (outputDestination is not null && !string.Equals(outputDestination, registeredOutput, pathComparison))
                throw new InvalidDataException("Refresh must retain the registered shader output.");
            outputDestination = registeredOutput;
            if (!string.Equals(Path.GetDirectoryName(destination), directory, pathComparison) ||
                !string.Equals(Path.GetDirectoryName(outputDestination), directory, pathComparison) ||
                !destination.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(destination) || !File.Exists(outputDestination))
                throw new InvalidDataException("Refresh must reuse the registered shader in the existing local shader directory.");
            localName = Path.GetFileNameWithoutExtension(destination);
        }
        if (outputDestination is not null &&
            (!string.Equals(Path.GetDirectoryName(outputDestination), directory, pathComparison) ||
             !outputDestination.EndsWith(ShaderAsset.PathSuffix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The shader output must remain in the existing local shader directory.");
        if (File.Exists(destination))
        {
            string? existing = JsonNode.Parse(File.ReadAllText(destination))?["Fields"]?["Id"]?.GetValue<string>();
            if (!Guid.TryParse(existing, out Guid existingId) || existingId != shaderId)
                throw new InvalidDataException("The destination shader belongs to a different identity.");
        }
        string sourceDirectory = Path.GetDirectoryName(sourceDefinitionPath)!;
        string sourceLayout = Path.Combine(sourceDirectory, "generated", "render_instance_layout.slang");
        string localLayout = Path.Combine(directory, "generated", "render_instance_layout.slang");
        if (!File.ReadAllBytes(sourceLayout).AsSpan().SequenceEqual(File.ReadAllBytes(localLayout)))
            throw new InvalidDataException("The shared material and local project use different generated instance layouts.");
        string sharedSource = Path.GetFullPath(Path.Combine(sourceDirectory, sourceStem + ".slang"));
        string localSource = Path.Combine(directory, localName + ".slang");
        string sharedText = File.ReadAllText(sharedSource);
        string shader;
        if (regenerateMaterial)
        {
            const string depthInputs = "[MaterialDepthInputs(\"AlbedoMap,MaterialSampler\")]";
            shader = CreateSurfaceShader(defaults, localName, cutoff, twoSided,
                trackDepthInputs: sharedText.Contains(depthInputs, StringComparison.Ordinal));
        }
        else if (string.Equals(sharedSource, localSource, pathComparison)) shader = sharedText;
        else
        {
            string include = sharedSource.Replace('\\', '/');
            if (include.IndexOfAny(['"', '\r', '\n']) >= 0)
                throw new InvalidDataException("The shared shader path cannot be represented by an include directive.");
            shader = "#include \"" + include + "\"\n";
        }
        if (identityTies && !shader.Contains("#define CLUSTER_VISIBILITY_IDENTITY_TIES 1", StringComparison.Ordinal))
            shader = "#define CLUSTER_VISIBILITY_IDENTITY_TIES 1\n" + shader;
        string? transparency = null;
        if (transparencySource >= 0)
        {
            if (regenerateMaterial)
                transparency = "import \"" + localName + "\";\n#define TRANSPARENCY_MATERIAL_TYPE SceneTransparencyMaterial_" + localName +
                    "\n#include \"cluster_transparency_entries.slang\"\n";
            else
            {
                // A copied wrapper can still name a type in the original shared module.
                // Preserve that declaration on repeated refresh rather than guessing from its local filename.
                transparency = File.ReadAllText(Path.Combine(sourceDirectory, sourceStem + "_transparency.slang"));
                transparency = Replace(transparency, "import \"" + sourceStem + "\";", "import \"" + localName + "\";");
            }
        }
        sourceNames[materialSource] = localName + ".slang";
        cancellationToken.ThrowIfCancellationRequested();
        if (transparency is not null)
        {
            sourceNames[transparencySource] = localName + "_transparency.slang";
            WriteIfChanged(Path.Combine(directory, localName + "_transparency.slang"), transparency);
        }
        WriteIfChanged(localSource, shader);
        WriteIfChanged(destination, definition.ToJsonString());
        Guid built = await project.BuildAsync(destination, outputDestination, cancellationToken);
        if (built != shaderId) throw new InvalidDataException("The cooked shader changed the requested identity.");
        return built;
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

    private static float Cutoff(GltfMaterial material) => StandardGltfSceneImporter.GetAlphaMaskCutoff(material);
    private static string ShaderName(float cutoff) => cutoff < 0 ? "scene_opaque"
        : "scene_mask_" + BitConverter.SingleToInt32Bits(cutoff).ToString("x8");
    private static string Replace(string source, string previous, string next) => source.Contains(previous)
        ? source.Replace(previous, next) : throw new InvalidDataException($"The sample shader integration changed: {previous}");
    private static void WriteIfChanged(string path, string content)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
    }


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
