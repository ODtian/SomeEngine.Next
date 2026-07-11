namespace SomeEngine.Graphics;

using System.Collections.ObjectModel;

/// <summary>
/// An immutable, queue-normalized set of published GPU completion points from one device.
/// It is suitable for carrying resource readiness across render-graph invocations without
/// exposing backend fence objects.
/// </summary>
public sealed class GpuCompletionSet
{
    private static readonly GpuCompletion[] NoCompletions = [];
    private readonly GpuCompletion[] _completions;
    private readonly ReadOnlyCollection<GpuCompletion> _view;

    public static GpuCompletionSet Empty { get; } = new(NoCompletions, normalized: true);

    public GpuCompletionSet(ReadOnlySpan<GpuCompletion> completions)
    {
        if (completions.IsEmpty)
        {
            _completions = NoCompletions;
            _view = Array.AsReadOnly(_completions);
            return;
        }

        DeviceDomain domain = default;
        Dictionary<QueueType, ulong> values = new();
        foreach (ref readonly GpuCompletion completion in completions)
        {
            if (!completion.IsValid)
                throw new ArgumentException("Every GPU completion in a set must be valid.", nameof(completions));
            if (!domain.IsValid) domain = completion.Domain;
            else if (completion.Domain != domain)
                throw new ArgumentException("A GPU completion set cannot span device domains.", nameof(completions));

            values[completion.Queue] = values.TryGetValue(completion.Queue, out ulong current)
                ? Math.Max(current, completion.Value)
                : completion.Value;
        }

        _completions = values.OrderBy(static pair => pair.Key)
            .Select(pair => new GpuCompletion(domain, pair.Key, pair.Value))
            .ToArray();
        _view = Array.AsReadOnly(_completions);
    }

    private GpuCompletionSet(GpuCompletion[] completions, bool normalized)
    {
        _ = normalized;
        _completions = completions;
        _view = Array.AsReadOnly(_completions);
    }

    public int Count => _completions.Length;
    public DeviceDomain Domain => _completions.Length == 0 ? default : _completions[0].Domain;
    public IReadOnlyList<GpuCompletion> Completions => _view;
    public ReadOnlySpan<GpuCompletion> AsSpan() => _completions;
    public GpuCompletion[] ToArray() => _completions.ToArray();
}
