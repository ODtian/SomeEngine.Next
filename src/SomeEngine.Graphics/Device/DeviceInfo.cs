using System.Collections.ObjectModel;

namespace SomeEngine.Graphics;

public enum BackendKind : byte
{
    Null,
    Direct3D12,
}

public readonly record struct DeviceInfo(
    string Name,
    BackendKind Backend,
    bool HardwareAccelerated,
    uint VendorId = 0,
    uint DeviceId = 0);

public enum ResourceHeapTier : byte
{
    Tier1 = 1,
    Tier2 = 2,
}

/// <summary>
/// Backend facts that can affect graph compilation. The object and its collections are immutable.
/// </summary>
public sealed class DeviceCompilationSnapshot
{
    private readonly ReadOnlyCollection<QueueType> _queues;

    public DeviceCompilationSnapshot(
        ulong semanticGeneration,
        ResourceHeapTier resourceHeapTier,
        IEnumerable<QueueType> queues,
        bool supportsEnhancedBarriers,
        bool supportsAsyncCompute,
        bool supportsCopyQueue)
    {
        if (semanticGeneration == 0) throw new ArgumentOutOfRangeException(nameof(semanticGeneration));
        ArgumentNullException.ThrowIfNull(queues);
        QueueType[] copy = queues.Distinct().ToArray();
        if (copy.Length == 0 || !copy.Contains(QueueType.Graphics))
        {
            throw new ArgumentException("A graphics queue is required.", nameof(queues));
        }

        SemanticGeneration = semanticGeneration;
        ResourceHeapTier = resourceHeapTier;
        _queues = Array.AsReadOnly(copy);
        SupportsEnhancedBarriers = supportsEnhancedBarriers;
        SupportsAsyncCompute = supportsAsyncCompute;
        SupportsCopyQueue = supportsCopyQueue;
    }

    public ulong SemanticGeneration { get; }
    public ResourceHeapTier ResourceHeapTier { get; }
    public IReadOnlyList<QueueType> Queues => _queues;
    public bool SupportsEnhancedBarriers { get; }
    public bool SupportsAsyncCompute { get; }
    public bool SupportsCopyQueue { get; }

    public bool Supports(QueueType queue) => _queues.Contains(queue);
}
