using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Identifies one executable entry point in one shader asset.</summary>
[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderRef
{
    [AssetReference(typeof(Shader))]
    public string? AssetGuid { get; init; }

    public string? EntryPoint { get; init; }

    public ShaderStage Stage { get; init; }

    internal static Guid Require(
        ShaderRef? shader,
        string owner,
        string field,
        ShaderStage? expectedStage = null)
    {
        if (shader is null)
            throw new InvalidDataException($"{owner} field '{field}' is missing.");
        if (!global::System.Guid.TryParse(
                shader.AssetGuid,
                out Guid guid)
            || (guid == System.Guid.Empty))
        {
            throw new InvalidDataException(
                $"{owner} field '{field}.AssetGuid' has invalid shader GUID '{shader.AssetGuid}'.");
        }
        if (string.IsNullOrWhiteSpace(shader.EntryPoint))
        {
            throw new InvalidDataException(
                $"{owner} field '{field}.EntryPoint' is missing.");
        }
        if (!Enum.IsDefined(shader.Stage))
        {
            throw new InvalidDataException(
                $"{owner} field '{field}.Stage' has invalid value '{shader.Stage}'.");
        }
        if (expectedStage.HasValue && shader.Stage != expectedStage.Value)
        {
            throw new InvalidDataException(
                $"{owner} field '{field}' must reference a {expectedStage.Value} shader entry.");
        }
        return guid;
    }
}
