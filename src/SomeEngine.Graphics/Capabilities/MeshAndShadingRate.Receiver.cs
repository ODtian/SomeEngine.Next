using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    void DispatchMesh(CommandContext context, in DispatchArguments arguments);
    void DispatchMeshIndirect(CommandContext context, in BufferRegion arguments);
    void SetShadingRate(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner);
    void SetShadingRateImage(CommandContext context, Texture? texture);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DispatchMesh(CommandContext context, in DispatchArguments arguments) =>
        Receiver.DispatchMesh(context, arguments);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DispatchMeshIndirect(CommandContext context, in BufferRegion arguments) =>
        Receiver.DispatchMeshIndirect(context, arguments);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetShadingRate(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner = ShadingRateCombiner.Passthrough,
        ShadingRateCombiner imageCombiner = ShadingRateCombiner.Passthrough) =>
        Receiver.SetShadingRate(context, rate, primitiveCombiner, imageCombiner);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetShadingRateImage(CommandContext context, Texture? texture) =>
        Receiver.SetShadingRateImage(context, texture);
}
