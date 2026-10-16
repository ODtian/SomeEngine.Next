using SlangShaderSharp;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.ClusterShadersBlock")]
[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Operations))]
public sealed partial class ClusterShadersBlock : AssetBlock
{
    public partial IList<ClusterShaderOperation> Operations { get; set; }

    protected override void ValidateContent() => Validate("shader settings");

    public ClusterShaders Create(
        string name,
        IReadOnlyDictionary<AssetId<ShaderAsset>, Shader> shaders)
    {
        using ClusterShadersBlock copy = Duplicate();
        copy.Validate(name);
        var result = new ClusterShaders
        {
            Name = name,
            Operations = copy.Operations.Select(operation => new ShaderOperation
            {
                Role = operation.Role,
                Shaders = operation.Shaders!
                    .Select(reference => shaders[reference.Asset])
                    .ToImmutableList(),
                Entries = operation.Shaders!
                    .Select(reference => reference.ResolveEntry(shaders))
                    .ToImmutableList(),
            }).ToImmutableList(),
        };
        foreach (ShaderOperation operation in result.Operations)
            ValidateEntryStages(operation);
        return result;
    }

    private static void ValidateEntryStages(ShaderOperation operation)
    {
        bool isCompute = operation.Entries is [{ Stage: SlangStage.Compute }];
        bool isRaster = operation.Entries.Count == 2 &&
            operation.Entries.Count(static entry => entry.Stage == SlangStage.Vertex) == 1 &&
            operation.Entries.Count(static entry => entry.Stage == SlangStage.Fragment) == 1;
        if (!isCompute && !isRaster)
        {
            throw new InvalidDataException(
                $"Cluster shader role '{operation.Role}' must resolve to one compute entry " +
                "or one vertex/fragment pair.");
        }
    }
}
