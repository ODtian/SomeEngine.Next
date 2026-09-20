using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

internal enum ClusterConservativeBounds : byte
{
    Finite = 1,
    Unbounded = 2,
}

internal static class ClusterGeometryContracts
{
    internal const string VertexEncodingAttribute = "VertexEncoding";
    internal const string ConservativeBoundsAttribute = "ConservativeBounds";

    internal static string RequireVertexEncoding(
        Shader shader,
        string entryPoint,
        string subject)
        => RequireSingleValue(shader, entryPoint, VertexEncodingAttribute, subject);

    internal static ClusterConservativeBounds RequireConservativeBounds(
        Shader shader,
        string entryPoint,
        string subject)
        => RequireSingleValue(shader, entryPoint, ConservativeBoundsAttribute, subject) switch
        {
            "finite" => ClusterConservativeBounds.Finite,
            "unbounded" => ClusterConservativeBounds.Unbounded,
            string value => throw new InvalidDataException(
                $"{subject} entry '{entryPoint}' declares unsupported conservative bounds '{value}'."),
        };

    private static string RequireSingleValue(
        Shader shader,
        string entryPoint,
        string attributeName,
        string subject)
    {
        string? result = null;
        foreach (ShaderEntryPointAttribute attribute in shader.EntryPointAttributes ?? [])
        {
            if (!string.Equals(attribute.Name, attributeName, StringComparison.Ordinal) ||
                !shader.TryEntry(attribute, entryPoint, out _))
            {
                continue;
            }
            if (attribute.Args is not { Count: 1 } args ||
                string.IsNullOrWhiteSpace(args[0]))
            {
                throw new InvalidDataException(
                    $"{subject} entry '{entryPoint}' has an invalid '{attributeName}' declaration.");
            }
            if (result is not null && !string.Equals(result, args[0], StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{subject} entry '{entryPoint}' has divergent '{attributeName}' declarations.");
            }
            result = args[0];
        }
        return result ?? throw new InvalidDataException(
            $"{subject} entry '{entryPoint}' must declare '{attributeName}'.");
    }
}
