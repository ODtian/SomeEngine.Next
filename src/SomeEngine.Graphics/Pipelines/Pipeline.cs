namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// </remarks>
public enum PipelineType : byte
{
    Graphics,
    Compute,
    Mesh,
    RayTracing,
    WorkGraph,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe for concurrent binding and immutable metadata reads. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// </remarks>
public abstract class Pipeline : DeviceResource
{
    internal Pipeline(
        Device device,
        PipelineType type,
        string? label)
        : base(device, label)
    {
        Type = type;
    }

    public PipelineType Type { get; }
}
