namespace SomeEngine.RenderGraph;

internal sealed class UploadPage
{
    internal required Buffer Buffer;
    internal required MappedBuffer Mapping;
    internal required ulong CommittedBytes;
    internal BufferBoundaryState[] BoundaryStates = [];
    internal GraphBufferId GraphBuffer;
    internal long Used;

    internal bool TryReserve(long size, out ulong offset)
    {
        long start = Interlocked.Add(ref Used, size) - size;
        offset = checked((ulong)start);
        return start <= (long)Buffer.Info.Size - size;
    }
}
