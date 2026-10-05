#ifndef CLUSTER_RASTER_TARGET_RESOURCES_SLANG
#define CLUSTER_RASTER_TARGET_RESOURCES_SLANG

#include "cluster_raster_target_view.slang"

StructuredBuffer<ClusterRasterTargetView> RasterTargetViews;
StructuredBuffer<uint> RasterTargetPageHierarchy;
StructuredBuffer<uint4> RasterTargetPageTable;
StructuredBuffer<uint2> RasterTargetReceiverMasks;

#endif
