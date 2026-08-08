namespace SomeEngine.Graphics;

public enum PipelineType : byte
{
    Graphics,
    Compute,
    Mesh,
    RayTracing,
    WorkGraph,
}

public readonly record struct PipelineSignature(
    ulong Word0,
    ulong Word1,
    ulong Word2,
    ulong Word3);

public abstract class Pipeline : DeviceResource
{
    internal Pipeline(
        Device device,
        PipelineType type,
        in PipelineSignature signature,
        ParameterBindingContractSet bindingContracts,
        string? label)
        : base(device, label)
    {
        Type = type;
        Signature = signature;
        BindingContracts = bindingContracts ?? throw new ArgumentNullException(nameof(bindingContracts));
    }

    public PipelineType Type { get; }
    public PipelineSignature Signature { get; }
    internal ParameterBindingContractSet BindingContracts { get; }
}
