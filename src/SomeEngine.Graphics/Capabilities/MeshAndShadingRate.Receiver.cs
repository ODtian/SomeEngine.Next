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
