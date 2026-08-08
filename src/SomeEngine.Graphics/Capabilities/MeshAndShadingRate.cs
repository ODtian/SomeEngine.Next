namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class MeshShaders : DeviceCapability
{
    internal MeshShaders(
        Device device,
        bool amplificationShaders,
        bool indirectDispatch,
        uint maximumThreadGroupCountX,
        uint maximumThreadGroupCountY,
        uint maximumThreadGroupCountZ,
        uint maximumTotalThreadGroupCount,
        uint maximumThreadsPerGroup,
        uint maximumPayloadSize,
        uint maximumSharedMemory,
        uint maximumOutputVertices,
        uint maximumOutputPrimitives)
        : base(device)
    {
        AmplificationShaders = amplificationShaders;
        IndirectDispatch = indirectDispatch;
        MaximumThreadGroupCountX = maximumThreadGroupCountX;
        MaximumThreadGroupCountY = maximumThreadGroupCountY;
        MaximumThreadGroupCountZ = maximumThreadGroupCountZ;
        MaximumTotalThreadGroupCount = maximumTotalThreadGroupCount;
        MaximumThreadsPerGroup = maximumThreadsPerGroup;
        MaximumPayloadSize = maximumPayloadSize;
        MaximumSharedMemory = maximumSharedMemory;
        MaximumOutputVertices = maximumOutputVertices;
        MaximumOutputPrimitives = maximumOutputPrimitives;
    }

    public bool AmplificationShaders { get; }
    public bool IndirectDispatch { get; }
    public uint MaximumThreadGroupCountX { get; }
    public uint MaximumThreadGroupCountY { get; }
    public uint MaximumThreadGroupCountZ { get; }
    public uint MaximumTotalThreadGroupCount { get; }
    public uint MaximumThreadsPerGroup { get; }
    public uint MaximumPayloadSize { get; }
    public uint MaximumSharedMemory { get; }
    public uint MaximumOutputVertices { get; }
    public uint MaximumOutputPrimitives { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum ShadingRate : byte
{
    Rate1x1,
    Rate1x2,
    Rate2x1,
    Rate2x2,
    Rate2x4,
    Rate4x2,
    Rate4x4,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum ShadingRateCombiner : byte
{
    Passthrough,
    Override,
    Minimum,
    Maximum,
    Sum,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class VariableRateShading : DeviceCapability
{
    private readonly ShadingRate[] _rates;
    private readonly ShadingRateCombiner[] _combiners;

    internal VariableRateShading(
        Device device,
        ReadOnlySpan<ShadingRate> rates,
        ReadOnlySpan<ShadingRateCombiner> combiners,
        bool perPrimitive,
        bool shadingRateImage,
        bool additionalRates,
        uint imageTileWidth,
        uint imageTileHeight)
        : base(device)
    {
        _rates = rates.ToArray();
        _combiners = combiners.ToArray();
        PerPrimitive = perPrimitive;
        ShadingRateImage = shadingRateImage;
        AdditionalRates = additionalRates;
        ImageTileWidth = imageTileWidth;
        ImageTileHeight = imageTileHeight;
    }

    public ReadOnlySpan<ShadingRate> Rates => _rates;
    public ReadOnlySpan<ShadingRateCombiner> Combiners => _combiners;
    public bool PerPrimitive { get; }
    public bool ShadingRateImage { get; }
    public bool AdditionalRates { get; }
    public uint ImageTileWidth { get; }
    public uint ImageTileHeight { get; }
}
