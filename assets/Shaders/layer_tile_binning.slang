#ifndef LAYER_TILE_BINNING_SLANG
#define LAYER_TILE_BINNING_SLANG

__exported import screen_binning;

struct LayerStageWork
{
    uint Tile;
    uint RasterStage;
    uint ClusterOffset;
    uint ClusterCount;
};

struct LayerTileBinningParameters
{
    uint TileCount;
    uint RasterStageCount;
    uint ListCapacity;
    uint Padding;
};

interface ILayerTileClusterSource
{
    static uint GetVisibleCluster(uint itemIndex);
    static ScreenBinningBounds GetTileBounds(uint itemIndex);
    static uint GetTileIndex(uint itemIndex, uint3 tile);
    static bool HasRasterStage(uint visibleCluster, uint rasterStage);
};

struct LayerTileBinningItem<TSource : ILayerTileClusterSource> : IScreenBinningItem
{
    static ScreenBinningBounds GetBounds(uint itemIndex) { return TSource.GetTileBounds(itemIndex); }
    static bool Intersects(uint itemIndex, uint3 tile) { return true; }
    static uint GetCellIndex(uint itemIndex, uint3 tile) { return TSource.GetTileIndex(itemIndex, tile); }
    static uint GetItemIndex(uint itemIndex) { return TSource.GetVisibleCluster(itemIndex); }
};

ConstantBuffer<LayerTileBinningParameters> LayerTileBinning;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LayerStageMembership;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LayerStageClusters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> LayerStageTileRanges;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LayerStageWorkOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<LayerStageWork> LayerStageWorks;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> LayerStageWorkRanges;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint3> LayerStageDispatch;

void MarkLayerRasterStages<TSource : ILayerTileClusterSource>(uint index)
{
    uint count = LayerTileBinning.RasterStageCount * LayerTileBinning.ListCapacity;
    if (index > count) return;
    uint member = 0u;
    if (index < count)
    {
        uint stage = index / LayerTileBinning.ListCapacity;
        uint visibleCluster = ScreenBinningIndices[index % LayerTileBinning.ListCapacity];
        if (visibleCluster != 0xffffffffu && TSource.HasRasterStage(visibleCluster, stage)) member = 1u;
        LayerStageClusters[index] = 0xffffffffu;
    }
    LayerStageMembership[index] = member;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void LayerBuildStageTileRanges(uint3 id : SV_DispatchThreadID)
{
    uint index = ScreenBinningThreadIndex(id);
    uint count = LayerTileBinning.RasterStageCount * LayerTileBinning.TileCount;
    if (index > count) return;
    uint occupied = 0u;
    if (index < count)
    {
        uint stage = index / LayerTileBinning.TileCount;
        uint tile = index % LayerTileBinning.TileCount;
        uint2 tileRange = ScreenBinningCells[tile];
        uint2 range = uint2(0u);
        if (tileRange.y != 0u)
        {
            uint first = stage * LayerTileBinning.ListCapacity + tileRange.x;
            uint offset = LayerStageMembership[first];
            range = uint2(offset, LayerStageMembership[first + tileRange.y] - offset);
        }
        LayerStageTileRanges[index] = range;
        occupied = range.y != 0u ? 1u : 0u;
        LayerStageWork emptyWork;
        emptyWork.Tile = emptyWork.RasterStage = 0xffffffffu;
        emptyWork.ClusterOffset = emptyWork.ClusterCount = 0u;
        LayerStageWorks[index] = emptyWork;
    }
    LayerStageWorkOffsets[index] = occupied;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void LayerFillStageClusters(uint3 id : SV_DispatchThreadID)
{
    uint index = ScreenBinningThreadIndex(id);
    uint count = LayerTileBinning.RasterStageCount * LayerTileBinning.ListCapacity;
    if (index < count && LayerStageMembership[index + 1u] != LayerStageMembership[index])
        LayerStageClusters[LayerStageMembership[index]] = ScreenBinningIndices[index % LayerTileBinning.ListCapacity];
}

[shader("compute")]
[numthreads(256, 1, 1)]
void LayerBuildStageWork(uint3 id : SV_DispatchThreadID)
{
    uint index = ScreenBinningThreadIndex(id);
    uint count = LayerTileBinning.RasterStageCount * LayerTileBinning.TileCount;
    if (index < count && LayerStageWorkOffsets[index + 1u] != LayerStageWorkOffsets[index])
    {
        uint2 range = LayerStageTileRanges[index];
        LayerStageWork work;
        work.Tile = index % LayerTileBinning.TileCount;
        work.RasterStage = index / LayerTileBinning.TileCount;
        work.ClusterOffset = range.x;
        work.ClusterCount = range.y;
        LayerStageWorks[LayerStageWorkOffsets[index]] = work;
    }
    if (index < LayerTileBinning.RasterStageCount)
    {
        uint first = LayerStageWorkOffsets[index * LayerTileBinning.TileCount];
        uint length = LayerStageWorkOffsets[(index + 1u) * LayerTileBinning.TileCount] - first;
        LayerStageWorkRanges[index] = uint2(first, length);
        LayerStageDispatch[index] = uint3(min(length, 65535u), max((length + 65534u) / 65535u, 1u), 1u);
    }
}

LayerStageWork LoadLayerStageWork(uint rasterStage, uint groupIndex)
{
    return LayerStageWorks[LayerStageWorkRanges[rasterStage].x + groupIndex];
}

LayerStageWork LoadLayerStageWork(uint rasterStage, uint3 group)
{
    return LoadLayerStageWork(rasterStage, group.x + group.y * 65535u);
}

struct TileClusterInput
{
    LayerStageWork Work;
    uint GetClusterCount() { return Work.ClusterCount; }
    uint ReadCluster(uint index) { return LayerStageClusters[Work.ClusterOffset + index]; }
};

#endif
