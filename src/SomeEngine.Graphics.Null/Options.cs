namespace SomeEngine.Graphics.Null;

/// <summary>Configuration for the deterministic CPU validation backend.</summary>
public sealed record Options
{
    public bool AutoCompleteSubmissions { get; init; } = true;
    public bool SupportsAsyncCompute { get; init; } = true;
    public bool SupportsCopyQueue { get; init; } = true;
    public bool SupportsEnhancedBarriers { get; init; } = true;
    public ResourceHeapTier ResourceHeapTier { get; init; } = ResourceHeapTier.Tier2;
    public string DeviceName { get; init; } = "Null Validation Device";
}

public readonly record struct Statistics(
    long HeapCreates,
    long BufferCreates,
    long TextureCreates,
    long CommandContextAcquires,
    long CommandListFinishes,
    long Submissions,
    long SubmissionWaits,
    long SubmittedCommandLists,
    long RecordedCommands,
    long ExecutedCopies,
    long Draws,
    long Dispatches,
    long GarbageCollections,
    long RetiredObjects);
