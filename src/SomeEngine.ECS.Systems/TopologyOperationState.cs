namespace SomeEngine.ECS.Systems;

internal sealed class TopologyOperationState
{
    private const int TerminalPending = 0;
    private const int TerminalSucceeded = 1;
    private const int TerminalFailed = 2;

    private StableQueryPartitionProof? _proof;
    private int _terminalState;

    internal void SetProof(StableQueryPartitionProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (Interlocked.CompareExchange(ref _proof, proof, null) is not null)
        {
            throw new InvalidOperationException(
                "Topology packet capture produced partition evidence more than once.");
        }
    }

    internal void MarkSucceeded()
    {
        if (Volatile.Read(ref _proof) is null)
        {
            throw new InvalidOperationException(
                "A topology operation cannot succeed without partition evidence.");
        }

        int previous = Interlocked.CompareExchange(
            ref _terminalState,
            TerminalSucceeded,
            TerminalPending);
        if (previous != TerminalPending)
        {
            throw new InvalidOperationException(
                "Topology operation terminal state was already published.");
        }
    }

    internal void MarkFailed() =>
        Interlocked.Exchange(ref _terminalState, TerminalFailed);

    internal StableQueryPartitionProof RequireSuccessfulProof()
    {
        int terminal = Volatile.Read(ref _terminalState);
        if (terminal == TerminalFailed)
        {
            throw new InvalidOperationException(
                "Topology partition evidence is unavailable because the transaction failed.");
        }
        if (terminal != TerminalSucceeded)
        {
            throw new InvalidOperationException(
                "Topology partition evidence is available only after successful transaction completion.");
        }

        return Volatile.Read(ref _proof)
            ?? throw new InvalidOperationException(
                "A successful topology transaction has no partition evidence.");
    }
}
