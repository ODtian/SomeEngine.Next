using SomeEngine.Serialization;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

[BinarySerializable(BinaryCompatibility.ExactSchema)]
public sealed partial record DependencyEntry
{
    public DependencyEntry()
    {
    }


    public string? Path { get; init; }
    public string? ContentHash { get; init; }
}

[BinarySerializable(BinaryCompatibility.ExactSchema)]
public sealed partial record ImportTrace
{
    public ImportTrace()
    {
    }


    public string? SourceGuid { get; init; }
    public string? SourcePath { get; init; }
    public string? SubAssetKey { get; init; }
    public string? ContentFingerprint { get; init; }
    public ImmutableList<DependencyEntry>? Dependencies { get; init; }
    public uint ImporterVersion { get; init; }
}



[BinarySerializable(BinaryCompatibility.ExactSchema), TextSerializable]
public sealed partial record ClusterShaderOperation
{
    public ClusterShaderOperation()
    {
    }


    public ClusterShaderOperationRole Role { get; init; }
    public ImmutableList<ShaderRef>? Shaders { get; init; }
}


[BinarySerializable(BinaryCompatibility.ExactSchema, LogicalName = "SomeEngine.Assets.Schema.CookedShaderProgram")]
public sealed partial record CookedShaderProgram
{
    public CookedShaderProgram()
    {
    }


    public string? Backend { get; init; }
    public ulong DataChunkKey { get; init; }
    public ulong DataDecodedLength { get; init; }
    public string? ContentHash { get; init; }


}
