using System.Runtime.CompilerServices;

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

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndirectCommandLayout CreateIndirectCommandLayout(
        Device device,
        in IndirectCommandLayoutDesc desc) =>
        Receiver.CreateIndirectCommandLayout(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count = null) =>
        Receiver.ExecuteIndirect(context, layout, arguments, maximumCommandCount, count);
}
