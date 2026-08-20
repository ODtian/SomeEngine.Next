namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    IndirectCommandLayout CreateIndirectCommandLayout(
        Device device,
        in IndirectCommandLayoutDesc desc);

    void ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count = null);
}
