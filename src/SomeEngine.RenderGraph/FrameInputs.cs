namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly Dictionary<GraphBufferInput, (int Buffer, BufferRange Range)> _bufferInputConnections = [];
    private readonly Dictionary<GraphTextureInput, (int Texture, uint Mips, uint Layers, Format Format)> _textureInputConnections = [];
    private readonly HashSet<GraphTextureInput> _changedTextureInputs = [];

    internal void ChangeTextureInput(GraphTextureInput input) => _changedTextureInputs.Add(input);

    private void UpdateInputConnections()
    {
        GraphStructureIndex structure = _frame.StructureIndex;
        foreach (GraphBufferInput input in _changedBufferInputs)
        {
            GraphBufferSlice value = _frame.ResolveBufferInput(input);
            var connection = (ResolveBuffer(value.Buffer.Value), value.Range);
            if (_bufferInputConnections.TryGetValue(input, out var previous) && previous == connection)
                continue;

            _bufferInputConnections[input] = connection;
            _rowChanges.Views.UnionWith(structure.BufferInputViews.GetValueOrDefault(input, []));
            _rowChanges.Accesses.UnionWith(structure.BufferInputAccesses.GetValueOrDefault(input, []));
        }

        foreach (GraphTextureInput input in _changedTextureInputs)
        {
            int texture = ResolveTexture(_frame.ResolveTextureInput(input).Value);
            ref readonly FrameTexture resource = ref CollectionsMarshal.AsSpan(_textures)[texture];
            var connection = (texture, resource.MipLevelCount, resource.ArrayLayerCount, resource.Format);
            if (_textureInputConnections.TryGetValue(input, out var previous) && previous == connection)
                continue;

            _textureInputConnections[input] = connection;
            _rowChanges.Views.UnionWith(structure.TextureInputViews.GetValueOrDefault(input, []));
            _rowChanges.Accesses.UnionWith(structure.TextureInputAccesses.GetValueOrDefault(input, []));
        }
        _changedTextureInputs.Clear();
    }
}
