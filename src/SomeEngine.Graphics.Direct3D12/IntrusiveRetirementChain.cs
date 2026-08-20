using System.Diagnostics;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>
/// A payload-owned link used after native work has been accepted and no allocation may remain.
/// </summary>
internal abstract class IntrusiveRetirementPayload<TPayload> : IDisposable
    where TPayload : IntrusiveRetirementPayload<TPayload>
{
    internal TPayload? RetirementNext;
    internal ulong RetirementCompletion;

    public abstract void Dispose();
}

/// <summary>
/// Allocation-free FIFO retirement storage. Nodes are created before native acceptance.
/// </summary>
internal struct IntrusiveRetirementChain<TPayload>
    where TPayload : IntrusiveRetirementPayload<TPayload>
{
    private TPayload? _head;
    private TPayload? _tail;

    internal bool HasAny => _head is not null;

    internal ulong Target => _tail?.RetirementCompletion ?? 0;

    internal void Append(TPayload payload, ulong completion)
    {
        Debug.Assert(payload.RetirementNext is null);
        Debug.Assert(_tail is null || completion >= _tail.RetirementCompletion);
        payload.RetirementCompletion = completion;
        payload.RetirementNext = null;
        if (_tail is null)
            _head = payload;
        else
            _tail.RetirementNext = payload;
        _tail = payload;
    }

    internal void Collect(ulong completed)
    {
        while (_head is { } payload && payload.RetirementCompletion <= completed)
        {
            RemoveHead(payload);
            payload.Dispose();
        }
    }

    internal void Abandon()
    {
        while (_head is { } payload)
        {
            RemoveHead(payload);
            payload.Dispose();
        }
    }

    private void RemoveHead(TPayload payload)
    {
        TPayload? next = payload.RetirementNext;
        _head = next;
        if (next is null)
            _tail = null;
        payload.RetirementNext = null;
        payload.RetirementCompletion = 0;
    }
}
