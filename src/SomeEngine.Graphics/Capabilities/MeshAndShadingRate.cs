namespace SomeEngine.Graphics;

public sealed class MeshShaders : DeviceCapability
{
    internal MeshShaders(
        Device device,
        bool amplificationShaders,
        bool indirectDispatch,
        uint maximumThreadGroupCountX,
        uint maximumThreadGroupCountY,
        uint maximumThreadGroupCountZ,
        uint maximumTotalThreadGroupCount,
        uint maximumThreadsPerGroup,
        uint maximumPayloadSize,
        uint maximumSharedMemory,
        uint maximumOutputVertices,
        uint maximumOutputPrimitives)
        : base(device)
    {
        AmplificationShaders = amplificationShaders;
        IndirectDispatch = indirectDispatch;
        MaximumThreadGroupCountX = maximumThreadGroupCountX;
        MaximumThreadGroupCountY = maximumThreadGroupCountY;
        MaximumThreadGroupCountZ = maximumThreadGroupCountZ;
        MaximumTotalThreadGroupCount = maximumTotalThreadGroupCount;
        MaximumThreadsPerGroup = maximumThreadsPerGroup;
        MaximumPayloadSize = maximumPayloadSize;
        MaximumSharedMemory = maximumSharedMemory;
        MaximumOutputVertices = maximumOutputVertices;
        MaximumOutputPrimitives = maximumOutputPrimitives;
    }

    public bool AmplificationShaders { get; }
    public bool IndirectDispatch { get; }
    public uint MaximumThreadGroupCountX { get; }
    public uint MaximumThreadGroupCountY { get; }
    public uint MaximumThreadGroupCountZ { get; }
    public uint MaximumTotalThreadGroupCount { get; }
    public uint MaximumThreadsPerGroup { get; }
    public uint MaximumPayloadSize { get; }
    public uint MaximumSharedMemory { get; }
    public uint MaximumOutputVertices { get; }
    public uint MaximumOutputPrimitives { get; }
}

public enum ShadingRate : byte
{
    Rate1x1,
    Rate1x2,
    Rate2x1,
    Rate2x2,
    Rate2x4,
    Rate4x2,
    Rate4x4,
}

public enum ShadingRateCombiner : byte
{
    Passthrough,
    Override,
    Minimum,
    Maximum,
    Sum,
}

public sealed class VariableRateShading : DeviceCapability
{
    private readonly ShadingRate[] _rates;
    private readonly ShadingRateCombiner[] _combiners;

    internal VariableRateShading(
        Device device,
        ReadOnlySpan<ShadingRate> rates,
        ReadOnlySpan<ShadingRateCombiner> combiners,
        bool perPrimitive,
        bool shadingRateImage,
        bool additionalRates,
        uint imageTileWidth,
        uint imageTileHeight)
        : base(device)
    {
        _rates = rates.ToArray();
        _combiners = combiners.ToArray();
        PerPrimitive = perPrimitive;
        ShadingRateImage = shadingRateImage;
        AdditionalRates = additionalRates;
        ImageTileWidth = imageTileWidth;
        ImageTileHeight = imageTileHeight;
    }

    public ReadOnlySpan<ShadingRate> Rates => _rates;
    public ReadOnlySpan<ShadingRateCombiner> Combiners => _combiners;
    public bool PerPrimitive { get; }
    public bool ShadingRateImage { get; }
    public bool AdditionalRates { get; }
    public uint ImageTileWidth { get; }
    public uint ImageTileHeight { get; }
}
