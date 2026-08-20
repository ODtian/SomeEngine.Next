namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct PipelineCacheDesc
{
    /// <summary>Creates a pipeline-cache description with optional entry, serialized-byte, and decoded-byte limits.</summary>
    /// <param name="data">A complete serialized cache envelope, or an empty span for a new cache.</param>
    /// <param name="label">An optional diagnostic label.</param>
    /// <param name="maximumEntryCount">
    /// The maximum resident entry count, or zero to select the backend default. A negative value is invalid
    /// and is rejected by <c>CreatePipelineCache</c>.
    /// </param>
    /// <param name="maximumByteCount">
    /// The maximum serialized envelope byte count, or zero to select the backend default. A negative value,
    /// or a nonzero value too small for the selected backend's empty envelope, is rejected by
    /// <c>CreatePipelineCache</c>.
    /// </param>
    /// <param name="maximumDecodedByteCount">
    /// The maximum total byte count of decoded variable section payloads, or zero to select the finite
    /// backend default. A negative value is rejected by <c>CreatePipelineCache</c>.
    /// </param>
    public PipelineCacheDesc(
        ReadOnlySpan<byte> data = default,
        string? label = null,
        int maximumEntryCount = 0,
        int maximumByteCount = 0,
        int maximumDecodedByteCount = 0)
    {
        Data = data;
        Label = label;
        MaximumEntryCount = maximumEntryCount;
        MaximumByteCount = maximumByteCount;
        MaximumDecodedByteCount = maximumDecodedByteCount;
    }

    /// <summary>Gets the caller-provided serialized cache envelope.</summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>Gets the optional diagnostic label.</summary>
    public string? Label { get; }

    /// <summary>Gets the maximum resident entry count; zero selects the backend default.</summary>
    public int MaximumEntryCount { get; }

    /// <summary>
    /// Gets the maximum total byte count of the serialized envelope, including its header, every section,
    /// and the envelope checksum; zero selects the backend default.
    /// </summary>
    public int MaximumByteCount { get; }

    /// <summary>
    /// Gets the maximum total byte count of decoded variable section payloads; zero selects the finite
    /// backend default. Fixed per-entry metadata is bounded by <see cref="MaximumEntryCount"/>.
    /// </summary>
    public int MaximumDecodedByteCount { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Pipeline-cache operations may run concurrently. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class PipelineCache : DeviceResource
{
    internal PipelineCache(Device device, string? label)
        : base(device, label)
    {
    }
}
