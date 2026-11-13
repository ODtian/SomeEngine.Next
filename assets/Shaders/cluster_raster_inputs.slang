#ifndef CLUSTER_RASTER_INPUTS_SLANG
#define CLUSTER_RASTER_INPUTS_SLANG

__exported import cluster_bin_common;

StructuredBuffer<uint> RasterSpillClusters;
StructuredBuffer<uint> RasterInputBinIndices;
ByteAddressBuffer RasterInputDrawArgs;
ByteAddressBuffer RasterInputReadOffset0;
ByteAddressBuffer RasterInputReadOffset1;
ByteAddressBuffer RasterInputReadOffset2;

enum RasterInputPath { Software, Hardware, All };

interface IClusterRasterInput
{
    static bool Spilled();
    static uint Count(uint batchCount);
    static bool Read(uint batch, uint item, uint visibleCapacity, out uint work);
    static bool AcceptTriangle(uint bin, uint materialOffset, uint localMaterial,
        bool target, uint work, RasterInputPath path);
};

struct RasterBatchesInput : IClusterRasterInput
{
    static bool Spilled() { return false; }
    static uint Count(uint batchCount) { return batchCount; }
    static bool Read(uint batch, uint item, uint visibleCapacity, out uint work)
    {
        work = batch;
        return RasterSpillClusters[RasterWorkVisibleIndex(batch)] == 0xffffffffu;
    }
    static bool AcceptTriangle(uint bin, uint materialOffset, uint localMaterial,
        bool target, uint work, RasterInputPath path) { return true; }
};

struct RasterSpillInput : IClusterRasterInput
{
    static bool Spilled() { return true; }
    static uint Count(uint batchCount)
    {
        CullDrawArgs args = LoadCullDrawArgs(RasterInputDrawArgs, 0u);
        return args.SWCount + args.HWCount;
    }
    static bool Read(uint batch, uint item, uint visibleCapacity, out uint work)
    {
        uint visible;
        bool software;
        DecodeVisibleCluster(RasterInputDrawArgs, RasterInputReadOffset0,
            RasterInputReadOffset1, RasterInputReadOffset2, visibleCapacity, item, visible, software);
        uint spilled = RasterSpillClusters[visible];
        work = spilled | (software ? 0x80000000u : 0u);
        return spilled != 0xffffffffu;
    }
    static bool AcceptTriangle(uint bin, uint materialOffset, uint localMaterial,
        bool target, uint work, RasterInputPath path)
    {
        uint count, stride;
        RasterInputBinIndices.GetDimensions(count, stride);
        uint stage = GetStageSlot(RasterInputBinIndices, materialOffset, localMaterial, count);
        if (stage == SLOT_INVALID || DecodeStageBin(stage) != bin)
            return false;
        bool software = (work & 0x80000000u) != 0u && (target || (stage & 0x50000000u) != 0x50000000u);
        return path == RasterInputPath.All || software == (path == RasterInputPath.Software);
    }
};

uint2 RasterInputTriangleRange<TInput : IClusterRasterInput>(ByteAddressBuffer pageHeap, DrawRequest request, uint work)
{
    uint first, end;
    if (TInput.Spilled())
    {
        uint cluster = request.PageOffset + pageHeap.Load(request.PageOffset + 16u) +
            DrawRequestClusterID(request) * PACKED_CLUSTER_STRIDE_BYTES;
        first = 0u;
        end = (pageHeap.Load(cluster + 60u) >> 8u) & 255u;
    }
    else DecodeRasterWorkRange(pageHeap, request, work, first, end);
    return uint2(first, end);
}

#endif
