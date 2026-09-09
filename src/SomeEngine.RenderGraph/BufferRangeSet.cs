namespace SomeEngine.RenderGraph;

/// <summary>
/// Immutable ranges that several passes can read without registering each range
/// again every frame. Construct a new set when the accessible contents change.
/// </summary>
public sealed class BufferRangeSet
{
    private readonly BufferRange[] _ranges;

    public BufferRangeSet(ReadOnlySpan<BufferRange> ranges) => _ranges = ranges.ToArray();

    public static BufferRangeSet Empty { get; } = new([]);
    public int Count => _ranges.Length;
    public ReadOnlySpan<BufferRange> Ranges => _ranges;
}

internal readonly record struct BufferRangeRead(
    GraphIdentity Pass,
    GraphIdentity Buffer,
    BufferRangeSet Ranges,
    PipelineSync Sync,
    ResourceAccess Access);
