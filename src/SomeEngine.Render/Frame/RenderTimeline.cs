using SomeEngine.Graphics;

namespace SomeEngine.Render.Frame;

/// <summary>
/// Opaque synchronization identity for one pipeline or persistent resource owner. Timeline state
/// belongs to the coordinator that creates it; the token never owns pipeline resources.
/// </summary>
internal sealed class RenderTimeline
{
    private readonly GenerationUse[] _generations;
    private readonly RenderDevice _renderDevice;

    private struct GenerationUse
    {
        internal QueueCompletion[] Fences;
        internal ulong LastFrame;
    }

    internal RenderTimeline(
        RenderFrameCoordinator owner,
        RenderDevice renderDevice,
        int generationCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generationCount);
        if (generationCount > sizeof(int) * 8)
            throw new ArgumentOutOfRangeException(nameof(generationCount));
        Owner = owner;
        _renderDevice = renderDevice;
        Device = renderDevice.Device;
        _generations = new GenerationUse[generationCount];
        for (int generation = 0; generation < generationCount; generation++)
            _generations[generation].Fences = [];
    }

    /// <summary>The graphics device owner to which this timeline is permanently bound.</summary>
    internal Device Device { get; }

    internal RenderFrameCoordinator Owner { get; }

    internal int GenerationCount => _generations.Length;

    internal ulong LastSubmissionSequence { get; set; }

    internal ulong PendingSubmissionSequence { get; set; }

    internal bool RetryRequired { get; set; }

    internal void RegisterSubmission(
        int generation,
        ReadOnlySpan<QueueCompletion> fences)
    {
        ValidateGeneration(generation);
        ref GenerationUse use = ref _generations[generation];
        use.Fences = RenderQueueCompletions.Merge(use.Fences, fences);
        // The coordinator publishes this frame's sequence after registering its readers.
        use.LastFrame = LastSubmissionSequence + 1;
    }

    internal int GetOldestSubmittedGeneration()
    {
        int oldest = 0;
        for (int generation = 1; generation < _generations.Length; generation++)
            if (_generations[generation].LastFrame < _generations[oldest].LastFrame)
                oldest = generation;
        return oldest;
    }

    internal bool IsGenerationAvailable(int generation)
    {
        ValidateGeneration(generation);
        ReadOnlySpan<QueueCompletion> fences = GetGenerationFences(generation);
        return fences.IsEmpty || _renderDevice.WaitAll(fences, TimeSpan.Zero);
    }

    internal bool WaitForGeneration(int generation, TimeSpan timeout)
    {
        ValidateGeneration(generation);
        ReadOnlySpan<QueueCompletion> fences = GetGenerationFences(generation);
        return fences.IsEmpty || _renderDevice.WaitAll(fences, timeout);
    }

    internal QueueCompletion[] GetGenerationFences(int generation)
    {
        ValidateGeneration(generation);
        return _generations[generation].Fences.ToArray();
    }

    private void ValidateGeneration(int generation)
    {
        if ((uint)generation >= (uint)_generations.Length)
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
