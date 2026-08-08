namespace SomeEngine.ECS;

/// <summary>
/// Internal, cumulative proof counters for work that exists only to preserve a
/// parent/endpoint-local semantic order.
/// </summary>
/// <remarks>
/// Payload byte counts cover only the managed value-type payload written to a
/// pending-placement dictionary. They intentionally exclude dictionary buckets,
/// entries, object headers, list/array capacity and allocator overhead.
/// Explicit order keys are not used: ordered shards encode order by position.
/// </remarks>
internal readonly record struct TopologyOrderDiagnostics(
    long OrderedPathDispatches,
    long OrderedIndexWorkUnits,
    long PlacementMetadataWrites,
    long PlacementMetadataPayloadBytesWritten,
    int LivePlacementMetadataRecords,
    long LivePlacementMetadataPayloadBytes,
    long ExplicitOrderKeyBytes);

internal sealed class TopologyOrderDiagnosticCounter
{
    private long _orderedPathDispatches;
    private long _orderedIndexWorkUnits;
    private long _placementMetadataWrites;
    private long _placementMetadataPayloadBytesWritten;

    internal TopologyOrderDiagnosticCounter CloneDetached() =>
        new()
        {
            _orderedPathDispatches = _orderedPathDispatches,
            _orderedIndexWorkUnits = _orderedIndexWorkUnits,
            _placementMetadataWrites = _placementMetadataWrites,
            _placementMetadataPayloadBytesWritten = _placementMetadataPayloadBytesWritten,
        };

    internal void RecordOrderedPath() => _orderedPathDispatches++;

    internal void RecordOrderedIndexWork(int units)
    {
        if (units < 0)
            throw new ArgumentOutOfRangeException(nameof(units));
        _orderedIndexWorkUnits += units;
    }

    internal void RecordPlacementMetadataWrite(int payloadBytes)
    {
        if (payloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        _placementMetadataWrites++;
        _placementMetadataPayloadBytesWritten += payloadBytes;
    }

    internal TopologyOrderDiagnostics Snapshot(
        int livePlacementMetadataRecords,
        int placementMetadataPayloadBytes)
    {
        if (livePlacementMetadataRecords < 0)
            throw new ArgumentOutOfRangeException(nameof(livePlacementMetadataRecords));
        if (placementMetadataPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(placementMetadataPayloadBytes));

        return new TopologyOrderDiagnostics(
            _orderedPathDispatches,
            _orderedIndexWorkUnits,
            _placementMetadataWrites,
            _placementMetadataPayloadBytesWritten,
            livePlacementMetadataRecords,
            checked((long)livePlacementMetadataRecords * placementMetadataPayloadBytes),
            ExplicitOrderKeyBytes: 0);
    }

    internal void Reset()
    {
        _orderedPathDispatches = 0;
        _orderedIndexWorkUnits = 0;
        _placementMetadataWrites = 0;
        _placementMetadataPayloadBytesWritten = 0;
    }
}
