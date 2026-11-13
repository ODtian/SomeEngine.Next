namespace SomeEngine.RenderGraph;

internal sealed partial class FrameRecording
{
    /// <summary>Reads the active frame's materialized identities, ranges and effective pass inputs.</summary>
    internal readonly struct FrameConnectionData(FrameRecording recording)
    {
        internal RenderGraphFrameState Frame => recording._frame;
        internal IReadOnlyList<FramePass> Passes => recording._passes;
        internal IReadOnlyList<FrameBuffer> Buffers => recording._buffers;
        internal IReadOnlyList<FrameTexture> Textures => recording._textures;
        internal IReadOnlyList<FrameResourceAccess> Accesses => recording._accesses;
        internal IReadOnlyList<List<int>> PassAccesses => recording._passAccesses;
        internal IReadOnlyDictionary<(GraphAccessTargetKind Kind, int Index), List<int>> ResourceAccesses => recording._resourceAccessLists;
        internal IReadOnlyList<int> DynamicPassRows => recording._dynamicPassRows;
        internal ulong[] BufferCoordinates => recording._bufferCoordinateScratch;
        internal int ResolvePass(GraphIdentity identity) => recording.ResolvePass(identity);
        internal int BuildBufferCoordinates(in FrameBuffer buffer, List<int> accesses) => recording.BuildBufferCoordinates(buffer, accesses);
        internal int BuildTextureCoordinates(in FrameTexture texture, List<int> accesses) => recording.BuildTextureCoordinates(texture, accesses);
        internal TextureRegionEnumerable TextureRegions(in FrameTexture texture, in TextureSubresourceRange range) => recording.TextureRegions(texture, range);
    }
}
