using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.Render;

/// <summary>
/// Long-lived render-side state for one Graphics Device. It owns per-device realizations and
/// retirement state, but it is not an RHI wrapper and never owns a RenderGraph.
/// </summary>
public sealed partial class RenderDevice : IDisposable
{
    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(30);

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly TimeSpan _disposeTimeout;
    private readonly object _completionGate = new();
    private readonly List<QueueCompletion> _submitted = [];
    private readonly List<RetiredResource> _retired = [];
    private readonly Dictionary<SamplerDesc, (Sampler Resource, int References)> _samplers = [];
    private bool _disposed;

    public RenderDevice(
        IGraphicsBackend backend,
        Device device,
        TimeSpan? disposeTimeout = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _disposeTimeout = disposeTimeout ?? DefaultDisposeTimeout;
        if (_disposeTimeout <= TimeSpan.Zero || _disposeTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(disposeTimeout));
    }

    public IGraphicsBackend Backend => _backend;
    public Device Device => _device;

    /// <summary>Retains a sampler shared by equal descriptions on this device.</summary>
    /// <remarks>
    /// Dispose the returned reference after its last use has been submitted and tracked.
    /// The Resource is borrowed; do not dispose it directly. The last reference removes
    /// the cached sampler and retires it after the tracked submissions complete.
    /// </remarks>
    public SamplerReference RetainSampler(SamplerDesc description)
    {
        lock (_completionGate)
        {
            ThrowIfDisposed();
            bool created = !_samplers.TryGetValue(description, out var value);
            if (created) value = (_backend.CreateSampler(_device, description), 0);
            try
            {
                var reference = new SamplerReference(value.Resource, () => ReleaseSampler(description));
                _samplers[description] = (value.Resource, checked(value.References + 1));
                return reference;
            }
            catch
            {
                if (created) value.Resource.Dispose();
                throw;
            }
        }
    }

    private void ReleaseSampler(SamplerDesc description)
    {
        lock (_completionGate)
        {
            if (_disposed) return;
            var value = _samplers[description];
            if (value.References > 1)
                _samplers[description] = (value.Resource, value.References - 1);
            else if (_submitted.Count == 0)
            {
                _samplers.Remove(description);
                value.Resource.Dispose();
            }
            else
            {
                _retired.Add(new RetiredResource(value.Resource, CaptureRetirementCompletions()));
                _samplers.Remove(description);
            }
        }
    }

    public sealed class SamplerReference : IDisposable
    {
        private Action? _release;
        internal SamplerReference(Sampler resource, Action release)
        {
            Resource = resource;
            _release = release;
        }
        public Sampler Resource { get; }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>
    /// Publishes Queue completions for work using this RenderDevice. The maximum value per Queue
    /// becomes the retirement boundary for resources replaced after this call.
    /// Completed resources are released by CollectCompleted at the frame boundary.
    /// </summary>
    public void TrackSubmission(ReadOnlySpan<QueueCompletion> completions)
    {
        ThrowIfDisposed();
        if (completions.IsEmpty)
            throw new ArgumentException("At least one Queue completion is required.", nameof(completions));
        lock (_completionGate)
        {
            foreach (ref readonly QueueCompletion completion in completions)
            {
                if (!ReferenceEquals(completion.Queue.Device, _device))
                {
                    throw new ArgumentException(
                        "A render completion belongs to another Graphics Device.",
                        nameof(completions));
                }
            }
            foreach (ref readonly QueueCompletion completion in completions)
                MergeCompletion(_submitted, completion);
        }
    }

    public void CollectCompleted()
    {
        ThrowIfDisposed();
        CollectTextureReleases();
        _backend.CollectCompleted(_device);
        lock (_completionGate)
            CollectCompletedLocked();
    }

    public void WaitForTrackedSubmissions(TimeSpan timeout)
    {
        ThrowIfDisposed();
        QueueCompletion[] completions;
        lock (_completionGate)
            completions = [.. _submitted];
        long started = Environment.TickCount64;
        foreach (QueueCompletion completion in completions)
        {
            TimeSpan remaining = Remaining(timeout, started);
            if (_backend.WaitCpu(completion, remaining) != WaitStatus.Completed)
            {
                throw new TimeoutException(
                    $"Render submission {completion.Queue}:{completion.Value} did not complete.");
            }
        }
        lock (_completionGate)
            CollectCompletedLocked();
        CollectTextureReleases();
    }

    internal bool IsComplete(in QueueCompletion completion)
        => _backend.IsComplete(completion);

    internal bool WaitAll(ReadOnlySpan<QueueCompletion> completions, TimeSpan timeout)
        => global::SomeEngine.Render.Frame.RenderQueueCompletions.WaitAll(
            _backend,
            completions,
            timeout);

    internal QueueCompletion[] CaptureSubmittedCompletions()
    {
        lock (_completionGate)
        {
            CollectCompletedLocked();
            return [.. _submitted];
        }
    }

    /// <summary>
    /// Defers disposal until every Queue completion currently tracked for this Device has finished.
    /// Callers must stop publishing future uses of <paramref name="value"/> before calling this.
    /// </summary>
    public void RetireAfterTrackedSubmissions(IDisposable value)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(value);
        lock (_completionGate)
        {
            CollectCompletedLocked();
            if (_submitted.Count == 0)
            {
                value.Dispose();
                return;
            }
            _retired.Add(new RetiredResource(value, CaptureRetirementCompletions()));
        }
    }

    internal void Retire(IDisposable value) => RetireAfterTrackedSubmissions(value);

    public void Dispose()
    {
        if (_disposed)
            return;
        WaitForTrackedSubmissions(_disposeTimeout);
        lock (_completionGate) _disposed = true;

        List<Exception>? failures = null;
        try
        {
            DisposeTextures();
            DisposeSamplers();
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }
        lock (_completionGate)
        {
            foreach (var sampler in _samplers.Values)
            {
                try { sampler.Resource.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            _samplers.Clear();
            foreach (RetiredResource retirement in _retired)
            {
                try { retirement.Value.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            _retired.Clear();
            _submitted.Clear();
        }
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private QueueCompletion[] CaptureRetirementCompletions()
    {
        if (_retired.Count != 0)
        {
            QueueCompletion[] completions = _retired[^1].Completions;
            if (completions.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(_submitted)))
                return completions;
        }
        return [.. _submitted];
    }

    private void CollectCompletedLocked()
    {
        for (int index = _retired.Count - 1; index >= 0; index--)
        {
            RetiredResource retirement = _retired[index];
            bool complete = true;
            foreach (QueueCompletion completion in retirement.Completions)
            {
                if (_backend.IsComplete(completion))
                    continue;
                complete = false;
                break;
            }
            if (!complete)
                continue;
            retirement.Value.Dispose();
            _retired.RemoveAt(index);
        }
        for (int index = _submitted.Count - 1; index >= 0; index--)
            if (_backend.IsComplete(_submitted[index]))
                _submitted.RemoveAt(index);
    }

    private static void MergeCompletion(List<QueueCompletion> values, QueueCompletion completion)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (!ReferenceEquals(values[index].Queue, completion.Queue))
                continue;
            if (completion.Value > values[index].Value)
                values[index] = completion;
            return;
        }
        values.Add(completion);
    }

    private static TimeSpan Remaining(TimeSpan timeout, long started)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return timeout;
        TimeSpan remaining = timeout -
            TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct RetiredResource(
        IDisposable Value,
        QueueCompletion[] Completions);
}
