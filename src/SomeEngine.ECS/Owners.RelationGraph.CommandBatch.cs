using System.Runtime.InteropServices;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Owners;

internal sealed partial class RelationGraph
{
    internal void BeginCommandBatch()
    {
        if (_commandBatchDepth != 0)
            throw new InvalidOperationException("Nested relationship command batches are not supported.");
        _commandBatchPayloads.Clear();
        _commandBatchDepth = 1;
    }

    internal void EndCommandBatch(World world, bool completed)
    {
        if (_commandBatchDepth != 1)
            throw new InvalidOperationException("No relationship command batch is active.");
        _commandBatchDepth = 0;

        if (!completed)
        {
            // Playback owns a detached structural candidate. Discarding that root is the
            // transaction rollback; rewriting the doomed candidate would only repeat hooks and
            // widen external side effects. Live query/job rollback remains a separate path.
            CommitDeferredWrites();
            EndTypedCommandBatches(completed: false);
            ClearCommandBatchState();
            return;
        }

        try
        {
            ValidateDeferredWrites(world);
            foreach (IRelationEndpointTracker tracker in _endpointTrackers)
            {
                if (_commandBatchPayloads.Contains(tracker.PayloadType))
                    tracker.ValidateDirty(world);
            }
            CommitDeferredWrites();
            EndTypedCommandBatches(completed: true);
            ClearCommandBatchState();
        }
        catch
        {
            CommitDeferredWrites();
            EndTypedCommandBatches(completed: false);
            ClearCommandBatchState();
            throw;
        }
    }

    private void EndTypedCommandBatches(bool completed)
    {
        foreach (IRelationTypeState state in _states)
        {
            if (_commandBatchPayloads.Contains(state.PayloadType))
                state.EndCommandBatch(completed);
        }
    }

    private bool TouchCommandBatch<T>(World world, RelationTypeState<T> state)
        where T : struct, IComponent
    {
        if (_commandBatchDepth == 0)
            return false;

        if (_commandBatchPayloads.Add(typeof(T)))
            state.BeginCommandBatch();
        return true;
    }

    /// <summary>
    /// Validates the complete deferred canonical image once, after every command in the stable
    /// playback order has run. This deliberately is not a per-command barrier: immediate
    /// operations mutate the last-applied generation, while deferred operations retain that
    /// externally visible adjacency until maintenance. Validating a partial deferred image would
    /// reject legal swaps and turn alternating deferred/immediate streams into quadratic scans.
    /// </summary>
    private static void ValidateCommandBatchFinalImage<T>(
        World world,
        RelationTypeState<T> state)
        where T : struct, IComponent
    {
        if (!state.RequiresCommandBatchValidationSynchronization)
            return;

        if (!state.HasDirtyEdges)
        {
            state.MarkCommandBatchValidationSynchronized();
            return;
        }

        List<RelationEndpointTransition<T>> transitions = CollectDirtyTransitions(
            world,
            state,
            out int transitionVisits);
        state.RecordCommandBatchValidationFullScan(transitionVisits);
        _ = state.PreviewBatch(CollectionsMarshal.AsSpan(transitions));
        state.MarkCommandBatchValidationSynchronized();
    }

    private void TouchCommandBatch(IRelationTypeState state)
    {
        if (_commandBatchDepth != 0 && _commandBatchPayloads.Add(state.PayloadType))
            state.BeginCommandBatch();
    }

    private void ClearCommandBatchState()
    {
        _commandBatchPayloads.Clear();
    }
}
