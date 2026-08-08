using SomeEngine.Graphics;

namespace SomeEngine.Render.Frame;

/// <summary>
/// Opaque synchronization identity for one pipeline or persistent resource owner. Timeline state
/// belongs to the coordinator that creates it; the token never owns pipeline resources.
/// </summary>
internal sealed class RenderTimeline
{
    private readonly QueueCompletion[] _generationFences;
    private readonly int[] _generationFenceCounts;
    private readonly IGraphicsBackend _backend;

    internal RenderTimeline(
        RenderFrameCoordinator owner,
        IGraphicsBackend backend,
        Device device,
        int generationCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generationCount);
        if (generationCount > sizeof(int) * 8)
            throw new ArgumentOutOfRangeException(nameof(generationCount));
        Owner = owner;
        _backend = backend;
        Device = device;
        _generationFences = new QueueCompletion[checked(generationCount * 3)];
        _generationFenceCounts = new int[generationCount];
    }

    /// <summary>The graphics device owner to which this timeline is permanently bound.</summary>
    internal Device Device { get; }

    internal RenderFrameCoordinator Owner { get; }

    internal int GenerationCount => _generationFenceCounts.Length;

    internal ulong LastSubmissionSequence { get; set; }

    internal ulong PendingSubmissionSequence { get; set; }

    internal bool RetryRequired { get; set; }

    internal void RegisterSubmission(
        int generation,
        ReadOnlySpan<QueueCompletion> fences)
    {
        ValidateGeneration(generation);
        QueueCompletion[] merged = RenderQueueCompletions.Merge(
            GetGenerationFences(generation),
            fences);
        Span<QueueCompletion> destination =
            _generationFences.AsSpan(checked(generation * 3), 3);
        destination.Clear();
        merged.CopyTo(destination);
        _generationFenceCounts[generation] = merged.Length;
    }

    internal bool IsGenerationAvailable(int generation)
    {
        ValidateGeneration(generation);
        ReadOnlySpan<QueueCompletion> fences = GetGenerationFences(generation);
        return fences.IsEmpty || RenderQueueCompletions.WaitAll(_backend, fences, TimeSpan.Zero);
    }

    internal bool WaitForGeneration(int generation, TimeSpan timeout)
    {
        ValidateGeneration(generation);
        ReadOnlySpan<QueueCompletion> fences = GetGenerationFences(generation);
        return fences.IsEmpty || RenderQueueCompletions.WaitAll(_backend, fences, timeout);
    }

    internal QueueCompletion[] GetGenerationFences(int generation)
    {
        ValidateGeneration(generation);
        return _generationFences
            .AsSpan(
                checked(generation * 3),
                _generationFenceCounts[generation])
            .ToArray();
    }

    private void ValidateGeneration(int generation)
    {
        if ((uint)generation >= (uint)_generationFenceCounts.Length)
            throw new ArgumentOutOfRangeException(nameof(generation));
    }
}

/// <summary>
/// Linear permission to mutate resources associated with one render timeline during prepare.
/// Only one lease for a given timeline may be active in a scope. Dispose the lease before
/// reacquiring that timeline or closing the prepare scope.
/// </summary>
internal sealed class RenderTimelineLease : IDisposable
{
    private RenderPrepareScope? _owner;
    private readonly RenderTimelineClaim _claim;
    private int _closed;

    internal RenderTimelineLease(RenderPrepareScope owner, in RenderTimelineClaim claim)
    {
        _owner = owner;
        _claim = claim;
    }

    /// <summary>
    /// The latest successfully submitted frame awaiting consumption by this timeline, or zero
    /// when this is a resource mutation without pending submitted history.
    /// </summary>
    internal ulong PendingSubmissionSequence => _claim.PendingSubmissionSequence;

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _closed, 1, 0) != 0)
            return;
        try
        {
            RenderPrepareScope owner = _owner
                ?? throw new ObjectDisposedException(nameof(RenderTimelineLease));
            owner.ReleaseLease(_claim);
            _owner = null;
        }
        catch
        {
            Volatile.Write(ref _closed, 0);
            throw;
        }
    }
}

internal readonly record struct RenderTimelineClaim(
    RenderTimeline Timeline,
    ulong PendingSubmissionSequence);
