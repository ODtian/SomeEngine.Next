namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Serialization.Binary.BinaryContract(
    global::SomeEngine.Serialization.Binary.BinaryCompatibility.ExactSchema)]
public sealed partial class ComputeKernelRef
{
    public ComputeKernelRef()
    {
    }

    public ShaderAssetRef? Shader { get; set; }

    public string? EntryPoint { get; set; }
}

[global::SomeEngine.Serialization.Binary.BinaryContract(
    global::SomeEngine.Serialization.Binary.BinaryCompatibility.ExactSchema)]
public sealed partial class ClusteredLightGridAlgorithm
{
    public ClusteredLightGridAlgorithm()
    {
    }

    public string? AssetGuid { get; set; }

    public string? Name { get; set; }

    public ComputeKernelRef? BuildGrid { get; set; }
}

[global::SomeEngine.Serialization.Binary.BinaryContract(
    global::SomeEngine.Serialization.Binary.BinaryCompatibility.ExactSchema)]
public sealed partial class VirtualShadowMapAlgorithm
{
    public VirtualShadowMapAlgorithm()
    {
    }

    public string? AssetGuid { get; set; }

    public string? Name { get; set; }

    public ComputeKernelRef? MarkPages { get; set; }

    public ComputeKernelRef? AllocatePages { get; set; }

    public ComputeKernelRef? ClearPages { get; set; }
}
