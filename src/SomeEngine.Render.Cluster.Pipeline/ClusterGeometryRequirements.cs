using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

internal enum ClusterConservativeBounds : byte
{
    Finite = 1,
    Unbounded = 2,
}

internal enum ClusterVisibilityDepth : byte { Opaque, Masked, Offset }

internal static class ClusterGeometryRequirements
{
    internal static ClusterConservativeBounds RequireConservativeBounds(
        Shader shader,
        string entryPoint,
        string subject) =>
        shader.RequireSlangEntryStringAttribute(0, entryPoint, "ConservativeBounds", subject) switch
        {
            "finite" => ClusterConservativeBounds.Finite,
            "unbounded" => ClusterConservativeBounds.Unbounded,
            string value => throw new InvalidDataException(
                $"{subject} entry '{entryPoint}' declares unsupported conservative bounds '{value}'."),
        };

    internal static HashSet<uint>? ReadMaterialDepthInputs(Material material, Shader shader, int targetIndex,
        params string?[] entryNames)
    {
        foreach (string? name in entryNames)
        {
            if (name is null) continue;
            if (!shader.TryGetSlangEntry(targetIndex, name, out EntryPointReflection entry)) return null;
            FunctionReflection function = entry.Function;
            bool declared = false;
            for (uint index = 0; index < function.AttributeCount; index++)
            {
                AttributeReflection attribute = function.GetAttribute(index);
                if (attribute.Name != "RasterMaterialInputs") continue;
                if (declared || attribute.ArgumentCount != 1 ||
                    attribute.GetArgumentValueString(0) != "evaluators") return null;
                declared = true;
            }
            if (!declared) return null;
        }

        ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
        var properties = new HashSet<uint>();
        foreach (string name in new[] { "VertexEvaluate", "DomainEvaluate", "VisibilityEvaluate" })
        {
            if (reflection.FindTypeByName(name) is not { } type) return null;
            bool declared = false;
            for (uint index = 0; index < type.AttributeCount; index++)
            {
                AttributeReflection attribute = type.GetAttribute(index);
                if (attribute.Name != "MaterialDepthInputs") continue;
                if (declared || attribute.ArgumentCount != 1) return null;
                declared = true;
                string names = attribute.GetArgumentValueString(0);
                if (names.Length == 0) continue;
                foreach (string propertyName in names.Split(',', StringSplitOptions.TrimEntries))
                {
                    // Expressions, misspellings and unavailable properties cannot grant reuse.
                    if (material.FindPropertyIndex(propertyName) is not { } property) return null;
                    properties.Add(property);
                }
            }
            if (!declared) return null;
        }
        return properties;
    }

    internal static ClusterVisibilityDepth RequireVisibilityDepth(Shader shader)
    {
        TypeReflection type = shader.GetSlangReflection(0).FindTypeByName("VisibilityEvaluate")
            ?? throw new InvalidDataException($"Shader '{shader.Name}' must expose its VisibilityEvaluate type.");
        for (uint index = 0; index < type.AttributeCount; index++)
        {
            AttributeReflection attribute = type.GetAttribute(index);
            if (attribute.Name != "VisibilityDepth") continue;
            return attribute.GetArgumentValueString(0) switch
            {
                "opaque" => ClusterVisibilityDepth.Opaque,
                "masked" => ClusterVisibilityDepth.Masked,
                "offset" => ClusterVisibilityDepth.Offset,
                _ => throw new InvalidDataException($"Shader '{shader.Name}' has an unsupported VisibilityDepth value."),
            };
        }
        throw new InvalidDataException($"Shader '{shader.Name}' must declare VisibilityDepth on its visibility type.");
    }

    internal static bool UsesBothRasterFaces(EntryPointReflection entry, bool targetOutput = false)
    {
        FunctionReflection function = entry.Function;
        for (uint index = 0; index < function.AttributeCount; index++)
        {
            AttributeReflection attribute = function.GetAttribute(index);
            if (attribute.Name != "RasterFaces") continue;
            return attribute.GetArgumentValueString(targetOutput ? 1u : 0u) switch
            {
                "front" => false,
                "both" => true,
                _ => throw new InvalidDataException($"Raster entry '{entry.Name}' declares unsupported RasterFaces."),
            };
        }
        return false;
    }

}
