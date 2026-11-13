using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Immutable;
using System.Text.Json;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Importers;

internal static partial class MaterialShaderEntries
{
    internal const string RasterEntriesHeader = "cluster_material_entries.slang";
    internal const string TessellationEntriesHeader = "cluster_tessellation_entries.slang";

    internal static ImmutableArray<string> SharedInputNames { get; } =
        ["surface_inputs", "shade_surface_entries", "surface_outputs", "layer_tile_raster", "layer_tile_tessellation", "layer_hardware_raster"];

    [GeneratedRegex(@"\bIMaterial\b")]
    private static partial Regex MaterialTypeName();

    internal static bool DeclaresMaterial(ShaderSourceBlock source, string definitionPath,
        IReadOnlyList<string>? includeDirectories)
    {
        if (source.SourceOnly) return false;
        string directory = Path.GetDirectoryName(definitionPath)!;
        foreach (string name in source.SourceNames)
            if (ShaderImports.FindFile(name, directory, [directory, .. includeDirectories ?? []]) is { } path &&
                MaterialTypeName().IsMatch(File.ReadAllText(path))) return true;
        return false;
    }

    internal static IReadOnlyList<ShaderSourceFile> Add(ShaderSourceBlock source, string definitionPath,
        IGlobalSession slang, SessionDesc settings, string name, CancellationToken cancellationToken)
    {
        if (source.HasSerializedSources || source.Inputs.Count != 0) return [];
        using ShaderSourceBlock materialSource = source.Duplicate();
        materialSource.SourceOnly = true;
        ShaderCompilation input = materialSource.CompileAsync(slang, settings, name, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        Shader shader = input.Shader;
        ShaderReflection reflection = shader.GetSlangReflection(0);
        if (reflection.FindTypeByName("IMaterial") is not { } materialInterface) return input.Files;
        var materials = new List<TypeReflection>();
        for (int index = 0; index < shader.SlangSourceUnitCount; index++)
            FindMaterials(shader.GetSlangSourceUnit(index).GetModuleReflection());
        if (materials.Count == 0) return input.Files;
        if (materials.Count != 1)
            throw new InvalidDataException($"Material shader '{name}' must declare one concrete IMaterial type.");
        materials[0].GetFullName(out ISlangBlob? typeName);
        string materialType = typeName!.AsString;
        string encoding = RequireStringAttribute(materials[0], "VertexEncoding");
        string bounds = RequireStringAttribute(materials[0], "ConservativeBounds");
        AttributeReflection? faces = FindAttribute(materials[0], "RasterFaces");
        string screenFaces = faces?.GetArgumentValueString(0) ?? "front";
        string shadowFaces = faces?.GetArgumentValueString(1) ?? screenFaces;
        TypeReflection domain = reflection.FindTypeByName(materialType + ".Domain")
            ?? throw new InvalidDataException($"Material '{materialType}' has no reflected domain type.");
        bool tessellated = FindAttribute(domain, "TessellatedSurface") is not null;
        TypeReflection visibility = reflection.FindTypeByName(materialType + ".Visibility")
            ?? throw new InvalidDataException($"Material '{materialType}' has no reflected visibility type.");
        _ = RequireStringAttribute(visibility, "VisibilityDepth");
        string entriesName = Path.GetFileNameWithoutExtension(definitionPath) + ".entries.slang";
        string path = Path.Combine(Path.GetDirectoryName(definitionPath)!, entriesName);
        ISession session = shader.SlangCode.Session;
        IModule? entries = session.LoadModuleFromSource(Path.GetFileNameWithoutExtension(entriesName), path,
            Slang.CreateBlob(Encoding.UTF8.GetBytes(Generate(source.SourceNames, materialType, encoding, bounds,
                screenFaces, shadowFaces, tessellated))),
            out ISlangBlob? diagnostics);
        if (entries is null)
            throw new InvalidDataException("Material entry generation failed: " + diagnostics?.AsString);
        SlangResult serialized = entries.Serialize(out ISlangBlob entryBytes);
        if (serialized.Failed) throw new InvalidDataException("Material entry serialization failed: " + serialized);
        for (int index = 0; index < input.SerializedSources.Count; index++)
            source.SetSerializedSource(index, input.SerializedSources[index].Span);
        int entryIndex = source.SourceNames.Count;
        source.SourceNames = [.. source.SourceNames, entriesName];
        source.SetSerializedSource(entryIndex, entryBytes.Buffer);
        source.Specializations = [.. source.Specializations, .. ClusterShadersBlock.CreateMaterialSpecializations()];
        var files = input.Files.ToDictionary(file => Path.GetFullPath(file.Path), StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entries.GetDependencyFileCount(); index++)
        {
            string dependency = Path.GetFullPath(entries.GetDependencyFilePath(index));
            if (!files.ContainsKey(dependency) && File.Exists(dependency))
                files.Add(dependency, new(dependency, ImportHash.FileSha256(dependency)));
        }
        return files.Values.ToArray();

        void FindMaterials(DeclReflection declaration)
        {
            if (declaration.Kind == DeclReflectionKind.Struct && reflection.IsSubType(declaration.Type, materialInterface))
                materials.Add(declaration.Type);
            if (declaration.Kind is DeclReflectionKind.Module or DeclReflectionKind.Namespace)
                for (uint index = 0; index < declaration.ChildrenCount; index++) FindMaterials(declaration.GetChild(index));
        }
    }

    private static AttributeReflection? FindAttribute(TypeReflection type, string name)
    {
        for (uint index = 0; index < type.AttributeCount; index++)
        {
            AttributeReflection attribute = type.GetAttribute(index);
            if (attribute.Name == name) return attribute;
        }
        return null;
    }

    private static string RequireStringAttribute(TypeReflection type, string name) =>
        (FindAttribute(type, name) ?? throw new InvalidDataException($"Material '{type.Name}' must declare '{name}'."))
            .GetArgumentValueString(0);

    private static string Generate(IList<string> sources, string materialType, string encoding, string bounds,
        string screenFaces, string shadowFaces, bool tessellated)
    {
        var text = new StringBuilder();
        foreach (string source in sources)
            text.Append("import \"").Append(Path.ChangeExtension(source, null).Replace('\\', '/')).AppendLine("\";");
        foreach (string input in SharedInputNames)
            if (input != "layer_tile_tessellation" || tessellated)
                text.Append("__exported import ").Append(input).AppendLine(";");
        text.Append("typedef ").Append(materialType).AppendLine(" BakedMaterial;");
        text.AppendLine("typedef BakedMaterial.Vertex VertexEvaluate;");
        text.AppendLine("typedef BakedMaterial.Domain DomainEvaluate;");
        text.AppendLine("typedef BakedMaterial.Visibility VisibilityEvaluate;");
        AppendRasterEntries(text, encoding, bounds, screenFaces, shadowFaces, tessellated);
        foreach (ClusterShadersBlock.MaterialSurfaceTarget target in ClusterShadersBlock.MaterialSurfaceTargets)
        {
            WriteGeometryAttributes();
            text.AppendLine("[shader(\"compute\")][numthreads(64,1,1)]");
            text.Append("void ").Append(target.EntryPoint).AppendLine("<let debugMode:uint>(");
            text.AppendLine("    uniform BakedMaterial.Vertex vertex, uniform BakedMaterial.Domain domain,");
            text.AppendLine("    uniform BakedMaterial surface, uint3 thread:SV_DispatchThreadID)");
            text.AppendLine("{");
            string inputType = target.Input + "<BakedMaterial.Vertex,BakedMaterial.Domain>";
            text.Append("    ").Append(inputType).AppendLine(" input;");
            text.Append("    ").Append(target.Output).AppendLine(" output;");
            text.Append("    ShadeSurface<BakedMaterial,").Append(inputType).Append(',').Append(target.Output)
                .AppendLine(",debugMode>(surface,vertex,domain,input,output,thread);");
            text.AppendLine("}");
        }
        AppendForwardShadeEntry(text, encoding, bounds, screenFaces, shadowFaces);
        WriteGeometryAttributes();
        text.AppendLine("[RasterMaterialInputs(\"evaluators\")]");
        text.AppendLine("[shader(\"compute\")][numthreads(32,1,1)]");
        text.AppendLine("void LayerRasterSoftware<TRequests:ILayerPageRequests>(uniform BakedMaterial.Vertex vertex,");
        if (tessellated) text.AppendLine("    uniform BakedMaterial.Domain domain,");
        text.AppendLine("    uniform BakedMaterial.Visibility visibility, uint3 group:SV_GroupID,uint lane:SV_GroupIndex)");
        text.AppendLine("{");
        text.AppendLine(tessellated
            ? $"    RasterizeLayerTileSurface<BakedMaterial.Vertex,BakedMaterial.Domain,BakedMaterial.Visibility,TRequests,{(screenFaces == "both" ? "true" : "false")}>(vertex,domain,visibility,group,lane);"
            : $"    RasterizeLayerTile<BakedMaterial.Vertex,BakedMaterial.Visibility,TRequests,{(screenFaces == "both" ? "true" : "false")}>(vertex,visibility,group,lane);");
        text.AppendLine("}");
        AppendLayerHardwareEntries(text, encoding, bounds, screenFaces, shadowFaces, tessellated);
        return text.ToString();

        void WriteGeometryAttributes() => AppendGeometryAttributes(text, encoding, bounds, screenFaces, shadowFaces);
    }

    private static void AppendGeometryAttributes(StringBuilder text, string encoding, string bounds,
        string screenFaces, string shadowFaces)
    {
        text.Append("[VertexEncoding(").Append(JsonSerializer.Serialize(encoding)).AppendLine(")]");
        text.Append("[ConservativeBounds(").Append(JsonSerializer.Serialize(bounds)).AppendLine(")]");
        text.Append("[RasterFaces(").Append(JsonSerializer.Serialize(screenFaces)).Append(',')
            .Append(JsonSerializer.Serialize(shadowFaces)).AppendLine(")]");
    }
}
