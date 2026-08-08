using System.Diagnostics;

namespace SomeEngine.ECS;

/// <summary>Immutable structural-transaction telemetry captured by one World.</summary>
public readonly struct WorldStructuralMetrics
{
    internal WorldStructuralMetrics(
        long started,
        long published,
        long aborted,
        long prepareTicks,
        long maximumPrepareTicks,
        long commitTicks,
        long maximumCommitTicks,
        long lifetimeTicks,
        long maximumLifetimeTicks,
        long clonedArchetypeShells,
        long maximumClonedArchetypeShells,
        long clonedChunkShells,
        long maximumClonedChunkShells,
        long clonedQueryMatches,
        long maximumClonedQueryMatches)
    {
        Started = started;
        Published = published;
        Aborted = aborted;
        PrepareTime = ToTimeSpan(prepareTicks);
        MaximumPrepareTime = ToTimeSpan(maximumPrepareTicks);
        CommitTime = ToTimeSpan(commitTicks);
        MaximumCommitTime = ToTimeSpan(maximumCommitTicks);
        Lifetime = ToTimeSpan(lifetimeTicks);
        MaximumLifetime = ToTimeSpan(maximumLifetimeTicks);
        ClonedArchetypeShells = clonedArchetypeShells;
        MaximumClonedArchetypeShells = maximumClonedArchetypeShells;
        ClonedChunkShells = clonedChunkShells;
        MaximumClonedChunkShells = maximumClonedChunkShells;
        ClonedQueryMatches = clonedQueryMatches;
        MaximumClonedQueryMatches = maximumClonedQueryMatches;
    }

    public long Started { get; }

    public long Published { get; }

    public long Aborted { get; }

    public TimeSpan PrepareTime { get; }

    public TimeSpan MaximumPrepareTime { get; }

    public TimeSpan CommitTime { get; }

    public TimeSpan MaximumCommitTime { get; }

    public TimeSpan Lifetime { get; }

    public TimeSpan MaximumLifetime { get; }

    /// <summary>Total archetype shells prepared across structural candidates.</summary>
    public long ClonedArchetypeShells { get; }

    /// <summary>Largest archetype-shell count prepared by one structural candidate.</summary>
    public long MaximumClonedArchetypeShells { get; }

    /// <summary>Total chunk shells prepared across structural candidates.</summary>
    public long ClonedChunkShells { get; }

    /// <summary>Largest chunk-shell count prepared by one structural candidate.</summary>
    public long MaximumClonedChunkShells { get; }

    /// <summary>Total compiled query matches remapped across structural candidates.</summary>
    public long ClonedQueryMatches { get; }

    /// <summary>Largest compiled-query match count remapped by one structural candidate.</summary>
    public long MaximumClonedQueryMatches { get; }

    private static TimeSpan ToTimeSpan(long ticks) =>
        TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
}

internal sealed class WorldStructuralMetricsState
{
    private long _started;
    private long _published;
    private long _aborted;
    private long _prepareTicks;
    private long _maximumPrepareTicks;
    private long _commitTicks;
    private long _maximumCommitTicks;
    private long _lifetimeTicks;
    private long _maximumLifetimeTicks;
    private long _clonedArchetypeShells;
    private long _maximumClonedArchetypeShells;
    private long _clonedChunkShells;
    private long _maximumClonedChunkShells;
    private long _clonedQueryMatches;
    private long _maximumClonedQueryMatches;

    internal void Started() => Interlocked.Increment(ref _started);

    internal void Prepared(long elapsedTicks, WorldStructureCloneMetrics cloneMetrics)
    {
        Interlocked.Add(ref _prepareTicks, elapsedTicks);
        RecordMaximum(ref _maximumPrepareTicks, elapsedTicks);
        Interlocked.Add(ref _clonedArchetypeShells, cloneMetrics.ArchetypeShells);
        Interlocked.Add(ref _clonedChunkShells, cloneMetrics.ChunkShells);
        Interlocked.Add(ref _clonedQueryMatches, cloneMetrics.QueryMatches);
        RecordMaximum(ref _maximumClonedArchetypeShells, cloneMetrics.ArchetypeShells);
        RecordMaximum(ref _maximumClonedChunkShells, cloneMetrics.ChunkShells);
        RecordMaximum(ref _maximumClonedQueryMatches, cloneMetrics.QueryMatches);
    }

    internal void Published(long commitTicks, long lifetimeTicks)
    {
        Interlocked.Increment(ref _published);
        Interlocked.Add(ref _commitTicks, commitTicks);
        Interlocked.Add(ref _lifetimeTicks, lifetimeTicks);
        RecordMaximum(ref _maximumCommitTicks, commitTicks);
        RecordMaximum(ref _maximumLifetimeTicks, lifetimeTicks);
    }

    internal void Aborted(long lifetimeTicks)
    {
        Interlocked.Increment(ref _aborted);
        Interlocked.Add(ref _lifetimeTicks, lifetimeTicks);
        RecordMaximum(ref _maximumLifetimeTicks, lifetimeTicks);
    }

    internal WorldStructuralMetrics Snapshot() =>
        new(
            Volatile.Read(ref _started),
            Volatile.Read(ref _published),
            Volatile.Read(ref _aborted),
            Volatile.Read(ref _prepareTicks),
            Volatile.Read(ref _maximumPrepareTicks),
            Volatile.Read(ref _commitTicks),
            Volatile.Read(ref _maximumCommitTicks),
            Volatile.Read(ref _lifetimeTicks),
            Volatile.Read(ref _maximumLifetimeTicks),
            Volatile.Read(ref _clonedArchetypeShells),
            Volatile.Read(ref _maximumClonedArchetypeShells),
            Volatile.Read(ref _clonedChunkShells),
            Volatile.Read(ref _maximumClonedChunkShells),
            Volatile.Read(ref _clonedQueryMatches),
            Volatile.Read(ref _maximumClonedQueryMatches));

    private static void RecordMaximum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
