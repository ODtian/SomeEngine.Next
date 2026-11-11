namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly List<BufferRange> _bufferReadRanges = [];

    private bool TryUpdateBufferReadRanges(in BufferStateStep step, PipelineSync sync, Queue? queue,
        bool retainDistinctQueueReaders, out BufferBoundaryState endpoint)
    {
        endpoint = default;
        _bufferReadRanges.Clear();
        foreach (int index in step.Accesses)
        {
            FrameResourceAccess access = _accesses[index];
            if (access.ResultContents.HasValue) return false;
            foreach (BufferRange range in access.BufferRanges) _bufferReadRanges.Add(range);
        }
        if (_bufferReadRanges.Count < 2) return false;
        _bufferReadRanges.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        for (int index = 1; index < _bufferReadRanges.Count; index++)
            if (_bufferReadRanges[index - 1].Offset + _bufferReadRanges[index - 1].Size > _bufferReadRanges[index].Offset)
                return false;

        _bufferContentScratch.Clear();
        _bufferContentScratch.AddRange(_bufferBoundaryScratch);
        _bufferContentScratch.Sort(static (left, right) => left.State.Range.Offset.CompareTo(right.State.Range.Offset));
        for (int index = 1; index < _bufferContentScratch.Count; index++)
        {
            BufferRange previous = _bufferContentScratch[index - 1].State.Range;
            if (previous.Offset + previous.Size > _bufferContentScratch[index].State.Range.Offset)
                return false;
        }

        // Apply the whole sparse read set in one pass over the existing contents.
        // Unread bytes keep their state and completion; every read gets this submission's completion.
        _bufferBoundaryScratch.Clear();
        int firstRange = 0;
        foreach (BufferStateValue previous in _bufferContentScratch)
        {
            BufferBoundaryState state = previous.State;
            if (retainDistinctQueueReaders && !ResourceAccessRules.Writes(state.Access) &&
                state.Queue is not null && queue is not null && !ReferenceEquals(state.Queue, queue))
            {
                _bufferBoundaryScratch.Add(previous);
                continue;
            }
            ulong start = state.Range.Offset, end = start + state.Range.Size;
            while (firstRange < _bufferReadRanges.Count &&
                _bufferReadRanges[firstRange].Offset + _bufferReadRanges[firstRange].Size <= start)
                firstRange++;
            for (int index = firstRange; index < _bufferReadRanges.Count && _bufferReadRanges[index].Offset < end; index++)
            {
                BufferRange range = _bufferReadRanges[index];
                if (range.Offset > start)
                    _bufferBoundaryScratch.Add(previous with { State = state with { Range = new(start, range.Offset - start) } });
                start = Math.Max(start, range.Offset + range.Size);
                if (start >= end) break;
            }
            if (start < end)
                _bufferBoundaryScratch.Add(previous with { State = state with { Range = new(start, end - start) } });
        }

        int firstState = 0;
        foreach (BufferRange range in _bufferReadRanges)
        {
            while (firstState < _bufferContentScratch.Count &&
                _bufferContentScratch[firstState].State.Range.Offset + _bufferContentScratch[firstState].State.Range.Size <= range.Offset)
                firstState++;
            BufferContentScope contentScope = BufferContentScope.FullRange;
            if (firstState < _bufferContentScratch.Count && Contains(_bufferContentScratch[firstState].State.Range, range))
                contentScope = _bufferContentScratch[firstState].State.ContentScope;
            endpoint = new(range, sync, step.Access, ResourceContentState.Defined, queue, null, contentScope);
            _bufferBoundaryScratch.Add(new(endpoint, -1));
        }
        _bufferBoundaryScratch.Sort(static (left, right) => left.State.Range.Offset.CompareTo(right.State.Range.Offset));
        _bufferContentScratch.Clear();
        return true;
    }
}
