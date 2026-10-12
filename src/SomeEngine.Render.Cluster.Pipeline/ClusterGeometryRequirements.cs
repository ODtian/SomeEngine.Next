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
    internal const string VertexEncodingAttribute = "VertexEncoding";
    internal const string ConservativeBoundsAttribute = "ConservativeBounds";

    internal static string RequireVertexEncoding(
        Shader shader,
        string entryPoint,
        string subject) =>
        RequireSingleValue(shader, entryPoint, VertexEncodingAttribute, subject);

    internal static ClusterConservativeBounds RequireConservativeBounds(
        Shader shader,
        string entryPoint,
        string subject) =>
        RequireSingleValue(shader, entryPoint, ConservativeBoundsAttribute, subject) switch
        {
            "finite" => ClusterConservativeBounds.Finite,
            "unbounded" => ClusterConservativeBounds.Unbounded,
            string value => throw new InvalidDataException(
                $"{subject} entry '{entryPoint}' declares unsupported conservative bounds '{value}'."),
        };

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

    internal static bool UsesPrimaryDepthReceiver(EntryPointReflection entry)
    {
        FunctionReflection function = entry.Function;
        for (uint index = 0; index < function.AttributeCount; index++)
        {
            AttributeReflection attribute = function.GetAttribute(index);
            if (attribute.Name != "PrimaryDepthReceiver") continue;
            if (attribute.ArgumentCount != 0)
                throw new InvalidDataException($"Entry '{entry.Name}' has an invalid PrimaryDepthReceiver declaration.");
            return true;
        }
        return false;
    }

    private static string RequireSingleValue(
        Shader shader,
        string entryPoint,
        string attributeName,
        string subject)
    {
        if (!shader.TryGetSlangEntry(
                0,
                entryPoint,
                out EntryPointReflection entry))
        {
            throw new InvalidDataException(
                $"{subject} has no Slang entry point named '{entryPoint}'.");
        }

        FunctionReflection function = entry.Function;
        string? result = null;
        for (uint index = 0; index < function.AttributeCount; index++)
        {
            AttributeReflection attribute = function.GetAttribute(index);
            if (attribute == AttributeReflection.Null ||
                !string.Equals(attribute.Name, attributeName, StringComparison.Ordinal))
            {
                continue;
            }
            if (attribute.ArgumentCount != 1)
            {
                throw new InvalidDataException(
                    $"{subject} entry '{entryPoint}' has an invalid '{attributeName}' declaration.");
            }
            string value = attribute.GetArgumentValueString(0);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException(
                    $"{subject} entry '{entryPoint}' has an empty '{attributeName}' value.");
            }
            if (result is not null && !string.Equals(result, value, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{subject} entry '{entryPoint}' has divergent '{attributeName}' declarations.");
            }
            result = value;
        }
        return result ?? throw new InvalidDataException(
            $"{subject} entry '{entryPoint}' must declare '{attributeName}'.");
    }
}
