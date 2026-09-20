namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _changedSynchronizationBindings = [];

    private void ObserveBufferSynchronization(int index, in FrameBuffer row)
    {
        var key = (GraphAccessTargetKind.Buffer, index);
        if (BufferSynchronizationChanged(index, row)) _changedSynchronizationResources.Add(key);
        else if (!ReferenceEquals(_buffers[index].Synchronization.Resource, row.Resource))
        {
            if (row.Ownership == RenderGraphResourceOwnership.CallerOwned) _changedSynchronizationBindings.Add(key);
            else _changedSynchronizationResources.Add(key);
        }
    }

    private void ObserveTextureSynchronization(int index, in FrameTexture row)
    {
        var key = (GraphAccessTargetKind.Texture, index);
        if (TextureSynchronizationChanged(index, row)) _changedSynchronizationResources.Add(key);
        else if (!ReferenceEquals(_textures[index].Synchronization.Resource, row.Resource))
        {
            if (row.Ownership == RenderGraphResourceOwnership.CallerOwned) _changedSynchronizationBindings.Add(key);
            else _changedSynchronizationResources.Add(key);
        }
    }

    private void RefreshSynchronizationBindings()
    {
        foreach (var key in _changedSynchronizationBindings)
        {
            if (_changedSynchronizationResources.Contains(key) || !ResourceExists(key) ||
                !_synchronizationByResource.TryGetValue(key, out ResourceSynchronization? synchronization))
                continue;
            Resource? previous;
            Resource? current;
            if (key.Kind == GraphAccessTargetKind.Buffer)
            {
                previous = _buffers[key.Index].Synchronization.Resource;
                current = _buffers[key.Index].Resource;
            }
            else
            {
                previous = _textures[key.Index].Synchronization.Resource;
                current = _textures[key.Index].Resource;
            }
            if (current is null || ReferenceEquals(previous, current)) continue;
            foreach ((int pass, PassSynchronization barriers) in synchronization.Passes)
            {
                if (current is Buffer buffer)
                {
                    foreach (ref BufferBarrier barrier in CollectionsMarshal.AsSpan(barriers.BeforeBuffers))
                        if (ReferenceEquals(barrier.Buffer, previous)) barrier = barrier with { Buffer = buffer };
                    foreach (ref BufferBarrier barrier in CollectionsMarshal.AsSpan(barriers.AfterBuffers))
                        if (ReferenceEquals(barrier.Buffer, previous)) barrier = barrier with { Buffer = buffer };
                }
                else if (current is Texture texture)
                {
                    foreach (ref TextureBarrier barrier in CollectionsMarshal.AsSpan(barriers.BeforeTextures))
                        if (ReferenceEquals(barrier.Texture, previous)) barrier = barrier with { Texture = texture };
                    foreach (ref TextureBarrier barrier in CollectionsMarshal.AsSpan(barriers.AfterTextures))
                        if (ReferenceEquals(barrier.Texture, previous)) barrier = barrier with { Texture = texture };
                }
                foreach (ref QueueAcquire acquire in CollectionsMarshal.AsSpan(barriers.Acquires))
                    if (ReferenceEquals(acquire.Resource, previous)) acquire = acquire with { Resource = current };
                foreach (ref QueueRelease release in CollectionsMarshal.AsSpan(barriers.Releases))
                    if (ReferenceEquals(release.Resource, previous)) release = release with { Resource = current };
                foreach (ref AliasingResource alias in CollectionsMarshal.AsSpan(barriers.AliasBefore))
                    if (ReferenceEquals(alias.Resource, previous)) alias = alias with { Resource = current };
                foreach (ref AliasingResource alias in CollectionsMarshal.AsSpan(barriers.AliasAfter))
                    if (ReferenceEquals(alias.Resource, previous)) alias = alias with { Resource = current };
                _changedSynchronizationPasses.Add(pass);
            }
            foreach (ref PendingQueueRelease release in CollectionsMarshal.AsSpan(synchronization.LateReleases))
            {
                int index = _lateReleases.IndexOf(release);
                release = release with { Resource = current };
                _lateReleases[index] = release;
            }
            _stateBindingResourcesChanged.Add(key);
            SaveSynchronizationInput(key);
        }
        _changedSynchronizationBindings.Clear();
    }
}
