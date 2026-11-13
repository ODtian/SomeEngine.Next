__exported import layer_tile_binning;
__exported import layer_hardware_binning;
__exported import screen_binning;
__exported import layer_composition_parameters;
__exported import cluster_structures;
__exported import cluster_bin_io;
__exported import cluster_view;
#include "cluster_instance_properties.slang"

StructuredBuffer<VisibleCluster> LayerVisibleClusters;
StructuredBuffer<uint4> LayerClusterTileWindows;
StructuredBuffer<ClusterViewUniforms> LayerPackedViews;
ByteAddressBuffer LayerFirstDraw;
ByteAddressBuffer LayerSecondDraw;
StructuredBuffer<uint> LayerMaterialRasterStages;
StructuredBuffer<uint2> LayerRasterMaterials;
ByteAddressBuffer PageHeap;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;

uint LayerVisibleClusterCapacity()
{
    uint count, stride;
    LayerVisibleClusters.GetDimensions(count, stride);
    return count;
}

uint LayerVisibleClusterCount()
{
    return min(LayerFirstDraw.Load(4u) + LayerFirstDraw.Load(8u) +
        LayerSecondDraw.Load(4u) + LayerSecondDraw.Load(8u), LayerVisibleClusterCapacity());
}

struct LayerCulledClusterSource : ILayerHardwareClusterSource
{
    static uint GetVisibleCluster(uint itemIndex)
    {
        if (itemIndex >= LayerVisibleClusterCount()) return 0xffffffffu;
        uint firstSoftware = LayerFirstDraw.Load(4u);
        uint firstHardware = LayerFirstDraw.Load(8u);
        uint secondSoftware = LayerSecondDraw.Load(4u);
        if (itemIndex < firstSoftware) return itemIndex;
        itemIndex -= firstSoftware;
        if (itemIndex < firstHardware) return LayerVisibleClusterCapacity() - itemIndex - 1u;
        itemIndex -= firstHardware;
        if (itemIndex < secondSoftware) return firstSoftware + itemIndex;
        itemIndex -= secondSoftware;
        return LayerVisibleClusterCapacity() - firstHardware - itemIndex - 1u;
    }

    static ScreenBinningBounds GetTileBounds(uint itemIndex)
    {
        uint visible = GetVisibleCluster(itemIndex);
        if (visible == 0xffffffffu) return (ScreenBinningBounds)0;
        uint4 window = LayerClusterTileWindows[visible];
        if ((window.z - window.x) * (window.w - window.y) > LayerCompositionUniforms.HardwareWindowTiles)
            return (ScreenBinningBounds)0;
        ScreenBinningBounds bounds;
        bounds.Minimum = uint3(window.xy, 0u);
        bounds.Size = uint3(window.zw - window.xy, 1u);
        return bounds;
    }

    static bool IsHardwareCluster(uint itemIndex)
    {
        uint visible = GetVisibleCluster(itemIndex);
        if (visible == 0xffffffffu) return false;
        uint4 window = LayerClusterTileWindows[visible];
        return (window.z - window.x) * (window.w - window.y) > LayerCompositionUniforms.HardwareWindowTiles;
    }

    static uint GetTileIndex(uint itemIndex, uint3 tile)
    {
        uint visible = GetVisibleCluster(itemIndex);
        uint view = DrawRequestViewIndex(LayerVisibleClusters[visible]);
        uint mainView = LayerPackedViews[view].TargetViewIndex;
        return (mainView * LayerCompositionUniforms.TileCountY + tile.y) *
            LayerCompositionUniforms.TileCountX + tile.x;
    }

    static bool HasRasterStage(uint visibleCluster, uint rasterStage)
    {
        DrawRequest request = VisibleClusterRequest(LayerVisibleClusters[visibleCluster]);
        uint cluster = request.PageOffset + PageHeap.Load(request.PageOffset + 16u) +
            DrawRequestClusterID(request) * PACKED_CLUSTER_STRIDE_BYTES;
        uint triangleCount = (PageHeap.Load(cluster + 60u) >> 8u) & 255u;
        ClusterMaterialRanges materials = LoadClusterMaterialRanges(PageHeap, cluster, request.PageOffset);
        uint materialOffset = LoadClusterMaterialSlotOffset(InstanceData,
            LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD), request.InstanceID);
        uint stageEntries, stageStride;
        LayerMaterialRasterStages.GetDimensions(stageEntries, stageStride);
        uint count = ClusterMaterialRangeCount(materials, triangleCount);
        for (uint index = 0u; index < count; index++)
        {
            uint2 range = ClusterMaterialRange(PageHeap, materials, triangleCount, index);
            uint encodedStage = GetStageSlot(LayerMaterialRasterStages, materialOffset, range.y, stageEntries);
            if (encodedStage == SLOT_INVALID) continue;
            uint address = materialOffset + range.y;
            if (LayerRasterMaterials[address].x != 0xffffffffu && DecodeStageBin(encodedStage) == rasterStage) return true;
        }
        return false;
    }
};

[shader("compute")]
[numthreads(256, 1, 1)]
void CountScreenItems(uint3 id : SV_DispatchThreadID)
{
    ScreenBinningCount<LayerTileBinningItem<LayerCulledClusterSource>>(ScreenBinningThreadIndex(id));
}

[shader("compute")]
[numthreads(256, 1, 1)]
void ExpandScreenItems(uint3 id : SV_DispatchThreadID)
{
    ScreenBinningExpand<LayerTileBinningItem<LayerCulledClusterSource>>(ScreenBinningThreadIndex(id));
}

[shader("compute")]
[numthreads(256, 1, 1)]
void FillScreenItems(uint3 id : SV_DispatchThreadID)
{
    ScreenBinningFillUnordered<LayerTileBinningItem<LayerCulledClusterSource>>(ScreenBinningThreadIndex(id));
}

[shader("compute")]
[numthreads(256, 1, 1)]
void LayerMarkRasterStages(uint3 id : SV_DispatchThreadID)
{
    MarkLayerRasterStages<LayerCulledClusterSource>(ScreenBinningThreadIndex(id));
}

[shader("compute")]
[numthreads(256, 1, 1)]
void LayerCountHardwareClusters(uint3 id : SV_DispatchThreadID)
{
    CountLayerHardwareClusters<LayerCulledClusterSource>(ScreenBinningThreadIndex(id));
}

[shader("compute")]
[numthreads(256, 1, 1)]
void LayerFillHardwareClusters(uint3 id : SV_DispatchThreadID)
{
    FillLayerHardwareClusters<LayerCulledClusterSource>(ScreenBinningThreadIndex(id));
}
