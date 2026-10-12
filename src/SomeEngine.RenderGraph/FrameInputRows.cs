namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly HashSet<GraphTextureInput> _changedTextureInputs = [];
    private readonly ConditionalWeakTable<GraphBufferInput, BufferInputLocation> _bufferInputLocations = new();

    private sealed class BufferInputLocation
    {
        internal int BufferIndex = -1;
        internal BufferRange Range;
    }

    internal void ChangeTextureInput(GraphTextureInput input) => _changedTextureInputs.Add(input);

    private BufferRange? ViewBufferRangeOverride(GraphView source) => source.CurrentBufferRange ??
        (source.Buffer.IsValid ? _frame.StructureIndex.Structure.Buffers.Get(source.Buffer).CurrentViewRange : null);

    private void UpdateInputRows()
    {
        GraphStructureIndex index = _frame.StructureIndex;
        foreach (GraphBufferInput input in _changedBufferInputs)
        {
            GraphBufferSlice value = _frame.ResolveBufferInput(input);
            int buffer = ResolveBuffer(value.Buffer.Value);
            BufferInputLocation location = _bufferInputLocations.GetValue(input, static _ => new());
            // A frame-local ID changes every frame even when the connection still
            // selects the same resource row and byte range. Resource replacement
            // already refreshes its views and parameters through that row.
            if (location.BufferIndex == buffer && location.Range == value.Range) continue;
            location.BufferIndex = buffer;
            location.Range = value.Range;
            foreach (GraphIdentity identity in index.BufferInputViews.GetValueOrDefault(input, []))
            {
                GraphView source = index.Structure.Views.Get(identity);
                BufferRange range = value.ResolveRange(ViewBufferRangeOverride(source) ?? source.BufferRange);
                if (FindInputView(identity, out int viewIndex))
                {
                    ref FrameView view = ref CollectionsMarshal.AsSpan(_views)[viewIndex];
                    if (ReferenceEquals(view.Definition, source) && view.BufferIndex == buffer && view.BufferRange == range)
                        continue;
                }
                _rowChanges.Views.Add(identity);
            }
            foreach (GraphIdentity identity in index.BufferInputAccesses.GetValueOrDefault(input, []))
            {
                PassResourceAccess source = index.Structure.Accesses.Get(identity);
                ChangeCommandData(source.Pass);
                if (!source.DynamicRange && FindInputAccess(identity, out int accessIndex))
                {
                    ref FrameResourceAccess access = ref CollectionsMarshal.AsSpan(_accesses)[accessIndex];
                    if (access.ResourceIndex == buffer && access.BufferRange == value.ResolveRange(source.BufferRange))
                        continue;
                }
                _rowChanges.Accesses.Add(identity);
            }
        }

        foreach (GraphTextureInput input in _changedTextureInputs)
        {
            GraphTextureId value = _frame.ResolveTextureInput(input);
            int texture = ResolveTexture(value.Value);
            foreach (GraphIdentity identity in index.TextureInputViews.GetValueOrDefault(input, []))
            {
                GraphView source = index.Structure.Views.Get(identity);
                TextureSubresourceRange range = _frame.ResolveTextureRange(value,
                    source.TextureRange.MipLevelCount == 0 ? null : source.TextureRange);
                if (FindInputView(identity, out int viewIndex))
                {
                    ref FrameView view = ref CollectionsMarshal.AsSpan(_views)[viewIndex];
                    if (ReferenceEquals(view.Definition, source) && view.TextureIndex == texture && view.TextureRange == range)
                    {
                        view.Texture = value.Value;
                        continue;
                    }
                }
                _rowChanges.Views.Add(identity);
            }
            foreach (GraphIdentity identity in index.TextureInputAccesses.GetValueOrDefault(input, []))
            {
                PassResourceAccess source = index.Structure.Accesses.Get(identity);
                TextureSubresourceRange range = _frame.ResolveTextureRange(value,
                    source.TextureRange.MipLevelCount == 0 ? null : source.TextureRange);
                if (!source.DynamicRange && FindInputAccess(identity, out int accessIndex))
                {
                    ref FrameResourceAccess access = ref CollectionsMarshal.AsSpan(_accesses)[accessIndex];
                    if (access.ResourceIndex == texture && access.TextureRange == range)
                    {
                        access.Target = value.Value;
                        continue;
                    }
                }
                _rowChanges.Accesses.Add(identity);
            }
        }
        _changedTextureInputs.Clear();
    }

    private bool FindInputView(GraphIdentity identity, out int index)
    {
        index = -1;
        return _rowsReady && _viewIndices.TryGetValue(identity, out index) &&
            (uint)index < (uint)_views.Count && _views[index].Identity == identity;
    }

    private bool FindInputAccess(GraphIdentity identity, out int index)
    {
        index = -1;
        return _rowsReady && _accessIndices.TryGetValue(identity, out index) &&
            (uint)index < (uint)_accesses.Count && _accesses[index].Identity == identity;
    }
}
