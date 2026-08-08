namespace SomeEngine.ECS.Serialization;

/// <summary>
/// Shared write-side admission for canonical topology records. Exporters reserve their exact
/// record count before allocating a captured graph, so a configured limit is a working-set gate
/// rather than a post-allocation observation.
/// </summary>
internal sealed class TopologyCaptureBudget
{
    private readonly long _maximumRecords;
    private long _consumedRecords;

    internal TopologyCaptureBudget(long maximumRecords)
    {
        if (maximumRecords < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        _maximumRecords = maximumRecords == 0 ? long.MaxValue : maximumRecords;
    }

    internal long ConsumedRecords => _consumedRecords;

    internal void ReserveRecords(long count, string stableName)
    {
        if (count < 0)
            throw new InvalidOperationException("A topology exporter reported a negative record count.");

        long next;
        try
        {
            next = checked(_consumedRecords + count);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"World serialization topology record count overflowed while capturing '{stableName}'.",
                exception);
        }

        if (next > _maximumRecords)
        {
            throw new InvalidOperationException(
                $"World serialization topology record count {next} exceeds the configured " +
                $"limit {_maximumRecords} while capturing '{stableName}'.");
        }

        _consumedRecords = next;
    }
}
