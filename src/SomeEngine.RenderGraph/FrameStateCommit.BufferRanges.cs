namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly List<BufferRange> _bufferUpdateRanges = [];

    private bool TryUpdateBufferRanges(in BufferStateStep step, PipelineSync sync, Queue? queue,
        bool retainDistinctQueueReaders, out BufferBoundaryState endpoint)
    {
        endpoint = default;
        _bufferUpdateRanges.Clear();
        ResourceContentState contents = ResourceContentState.Defined;
        BufferContentScope writeScope = BufferContentScope.FullRange;
        bool first = true;
        foreach (int index in step.Accesses)
        {
            FrameResourceAccess access = _accesses[index];
            if (step.Writes)
            {
                if (access.Mode is not (GraphAccessMode.Write or GraphAccessMode.ReadWrite)) return false;
                if (access.Mode == GraphAccessMode.Write &&
                    access.Coverage != WriteCoverage.Complete && !access.ResultContents.HasValue)
                    return false;
                ResourceContentState written = access.ResultContents ?? ResourceContentState.Defined;
                BufferContentScope scope = written == ResourceContentState.Defined &&
                    (access.ResultContents.HasValue || access.Mode == GraphAccessMode.ReadWrite)
                    ? access.BufferContentScope : BufferContentScope.FullRange;
                if (!first && (written != contents || scope != writeScope)) return false;
                contents = written;
                writeScope = scope;
            }
            else if (access.ResultContents.HasValue) return false;
            first = false;
            foreach (BufferRange range in access.BufferRanges) _bufferUpdateRanges.Add(range);
        }
        if (_bufferUpdateRanges.Count < 2) return false;
        _bufferUpdateRanges.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        if (step.Writes && writeScope == BufferContentScope.FullRange)
        {
            int count = 0;
            for (int index = 0; index < _bufferUpdateRanges.Count; index++)
            {
                BufferRange range = _bufferUpdateRanges[index];
                if (count != 0 && _bufferUpdateRanges[count - 1].Offset + _bufferUpdateRanges[count - 1].Size >= range.Offset)
                {
                    BufferRange previous = _bufferUpdateRanges[count - 1];
                    _bufferUpdateRanges[count - 1] = new(previous.Offset,
                        Math.Max(previous.Offset + previous.Size, range.Offset + range.Size) - previous.Offset);
                }
                else _bufferUpdateRanges[count++] = range;
            }
            _bufferUpdateRanges.RemoveRange(count, _bufferUpdateRanges.Count - count);
        }
        else
            for (int index = 1; index < _bufferUpdateRanges.Count; index++)
                if (_bufferUpdateRanges[index - 1].Offset + _bufferUpdateRanges[index - 1].Size > _bufferUpdateRanges[index].Offset)
                    return false;

        _bufferContentScratch.Clear();
        _bufferContentScratch.AddRange(_bufferBoundaryScratch);
        _bufferContentScratch.Sort(static (left, right) => left.State.Range.Offset.CompareTo(right.State.Range.Offset));
        if (!step.Writes)
            for (int index = 1; index < _bufferContentScratch.Count; index++)
            {
                BufferRange previous = _bufferContentScratch[index - 1].State.Range;
                if (previous.Offset + previous.Size > _bufferContentScratch[index].State.Range.Offset)
                    return false;
            }

        // All accesses in a step share one pass and submission completion. Cover their
        // ranges together, retaining the existing contents and completion of untouched bytes.
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
            while (firstRange < _bufferUpdateRanges.Count &&
                _bufferUpdateRanges[firstRange].Offset + _bufferUpdateRanges[firstRange].Size <= start)
                firstRange++;
            for (int index = firstRange; index < _bufferUpdateRanges.Count && _bufferUpdateRanges[index].Offset < end; index++)
            {
                BufferRange range = _bufferUpdateRanges[index];
                if (range.Offset > start)
                    _bufferBoundaryScratch.Add(previous with { State = state with { Range = new(start, range.Offset - start) } });
                start = Math.Max(start, range.Offset + range.Size);
                if (start >= end) break;
            }
            if (start < end)
                _bufferBoundaryScratch.Add(previous with { State = state with { Range = new(start, end - start) } });
        }

        int firstState = 0;
        foreach (BufferRange range in _bufferUpdateRanges)
        {
            BufferContentScope contentScope = writeScope;
            if (!step.Writes)
            {
                while (firstState < _bufferContentScratch.Count &&
                    _bufferContentScratch[firstState].State.Range.Offset + _bufferContentScratch[firstState].State.Range.Size <= range.Offset)
                    firstState++;
                contentScope = firstState < _bufferContentScratch.Count && Contains(_bufferContentScratch[firstState].State.Range, range)
                    ? _bufferContentScratch[firstState].State.ContentScope : BufferContentScope.FullRange;
            }
            endpoint = new(range, sync, step.Access, contents, queue, null, contentScope);
            _bufferBoundaryScratch.Add(new(endpoint, -1));
        }
        _bufferBoundaryScratch.Sort(static (left, right) => left.State.Range.Offset.CompareTo(right.State.Range.Offset));
        _bufferContentScratch.Clear();
        return true;
    }
}
