namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private void ChangeSynchronization((GraphAccessTargetKind Kind, int Index) resource, bool bindingOnly = false)
    {
        ref bool change = ref CollectionsMarshal.GetValueRefOrAddDefault(_synchronizationChanges, resource, out bool found);
        change = found ? change && bindingOnly : bindingOnly;
    }

    private void ChangePassSynchronization(int pass)
    {
        if (pass >= _passAccesses.Length || _passAccesses[pass] is not { } accesses) return;
        foreach (int index in accesses)
        {
            FrameResourceAccess access = _accesses[index];
            ChangeSynchronization((access.TargetKind, access.ResourceIndex));
        }
    }

    private void ObserveBufferSynchronization(int index, in FrameBuffer row)
    {
        bool stateChanged = BufferSynchronizationStateChanged(index, row);
        if (stateChanged || !ReferenceEquals(_buffers[index].Synchronization.Resource, row.Resource))
            ChangeSynchronization((GraphAccessTargetKind.Buffer, index), bindingOnly: !stateChanged);
    }

    private void ObserveTextureSynchronization(int index, in FrameTexture row)
    {
        bool stateChanged = TextureSynchronizationStateChanged(index, row);
        if (stateChanged || !ReferenceEquals(_textures[index].Synchronization.Resource, row.Resource))
            ChangeSynchronization((GraphAccessTargetKind.Texture, index), bindingOnly: !stateChanged);
    }

    private bool TryRebindSynchronization((GraphAccessTargetKind Kind, int Index) key, ResourceSynchronization synchronization)
    {
        Resource? resource;
        if (key.Kind == GraphAccessTargetKind.Buffer) resource = _buffers[key.Index].Resource;
        else if (key.Kind == GraphAccessTargetKind.Texture) resource = _textures[key.Index].Resource;
        else return false;
        if (resource is null) return false;
        foreach (PassSynchronization pass in synchronization.Passes.Values)
            if (pass.AliasBefore.Count != 0 || pass.AliasAfter.Count != 0) return false;

        // Ranges, queue transfers and pass dependencies are already valid here.
        // A different backing resource only replaces the resource references.
        foreach (var (index, pass) in synchronization.Passes)
        {
            if (resource is Buffer buffer)
            {
                Span<BufferBarrier> before = CollectionsMarshal.AsSpan(pass.BeforeBuffers);
                for (int item = 0; item < before.Length; item++) before[item] = before[item] with { Buffer = buffer };
                Span<BufferBarrier> after = CollectionsMarshal.AsSpan(pass.AfterBuffers);
                for (int item = 0; item < after.Length; item++) after[item] = after[item] with { Buffer = buffer };
            }
            else
            {
                Texture texture = (Texture)resource;
                Span<TextureBarrier> before = CollectionsMarshal.AsSpan(pass.BeforeTextures);
                for (int item = 0; item < before.Length; item++) before[item] = before[item] with { Texture = texture };
                Span<TextureBarrier> after = CollectionsMarshal.AsSpan(pass.AfterTextures);
                for (int item = 0; item < after.Length; item++) after[item] = after[item] with { Texture = texture };
            }
            Span<QueueAcquire> acquires = CollectionsMarshal.AsSpan(pass.Acquires);
            for (int item = 0; item < acquires.Length; item++) acquires[item] = acquires[item] with { Resource = resource };
            Span<QueueRelease> releases = CollectionsMarshal.AsSpan(pass.Releases);
            for (int item = 0; item < releases.Length; item++) releases[item] = releases[item] with { Resource = resource };
            _changedSynchronizationPasses.Add(index);
        }
        Span<PendingQueueRelease> late = CollectionsMarshal.AsSpan(synchronization.LateReleases);
        for (int item = 0; item < late.Length; item++)
        {
            _lateReleases.Remove(late[item]);
            late[item] = late[item] with { Resource = resource };
            _lateReleases.Add(late[item]);
        }
        if (resource is Buffer value)
        {
            ref FrameBuffer buffer = ref CollectionsMarshal.AsSpan(_buffers)[key.Index];
            buffer.Synchronization = buffer.Synchronization with { Resource = value };
        }
        else
        {
            ref FrameTexture texture = ref CollectionsMarshal.AsSpan(_textures)[key.Index];
            texture.Synchronization = texture.Synchronization with { Resource = (Texture)resource };
        }
        return true;
    }
}
