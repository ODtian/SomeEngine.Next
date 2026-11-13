#ifndef LAYER_HARDWARE_BINNING_SLANG
#define LAYER_HARDWARE_BINNING_SLANG

__exported import layer_tile_binning;
__exported import screen_binning;
__exported import visbuffer_encoding;

interface ILayerHardwareClusterSource : ILayerTileClusterSource
{
    static bool IsHardwareCluster(uint itemIndex);
};

struct LayerHardwareBinningParameters
{
    uint ItemCapacity, RasterStageCount, Padding0, Padding1;
};
ConstantBuffer<LayerHardwareBinningParameters> LayerHardwareBinning;
RWStructuredBuffer<uint> LayerHardwareMembership;
RWStructuredBuffer<uint> LayerHardwareClusters;
RWStructuredBuffer<uint2> LayerHardwareRanges;
RWStructuredBuffer<uint4> LayerHardwareDrawArguments;
RWStructuredBuffer<uint3> LayerHardwareDispatchArguments;

void CountLayerHardwareClusters<TSource : ILayerHardwareClusterSource>(uint index)
{
    uint count = LayerHardwareBinning.ItemCapacity * LayerHardwareBinning.RasterStageCount;
    if (index > count) return;
    uint member = 0u;
    if (index < count)
    {
        uint item = index % LayerHardwareBinning.ItemCapacity;
        uint visible = TSource.GetVisibleCluster(item);
        if (visible != 0xffffffffu && TSource.IsHardwareCluster(item) &&
            TSource.HasRasterStage(visible, index / LayerHardwareBinning.ItemCapacity)) member = 1u;
        LayerHardwareClusters[index] = 0xffffffffu;
    }
    LayerHardwareMembership[index] = member;
}

void FillLayerHardwareClusters<TSource : ILayerHardwareClusterSource>(uint index)
{
    uint count = LayerHardwareBinning.ItemCapacity * LayerHardwareBinning.RasterStageCount;
    if (index < count && LayerHardwareMembership[index + 1u] != LayerHardwareMembership[index])
        LayerHardwareClusters[LayerHardwareMembership[index]] = TSource.GetVisibleCluster(index % LayerHardwareBinning.ItemCapacity);
    if (index < LayerHardwareBinning.RasterStageCount)
    {
        uint first = LayerHardwareMembership[index * LayerHardwareBinning.ItemCapacity];
        uint length = LayerHardwareMembership[(index + 1u) * LayerHardwareBinning.ItemCapacity] - first;
        LayerHardwareRanges[index] = uint2(first, length);
        // The visibility identity fixes the cluster triangle range. Each instance
        // reads its actual count before evaluating a user vertex.
        LayerHardwareDrawArguments[index] = uint4((1u << VISBUFFER_TRIANGLE_BITS) * 3u, length, 0u, 0u);
        LayerHardwareDispatchArguments[index] = uint3(min(length, 65535u), max((length + 65534u) / 65535u, 1u), 1u);
    }
}

#endif
