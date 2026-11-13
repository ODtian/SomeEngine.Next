__exported import common;
__exported import cluster_structures;
__exported import visbuffer_encoding;
__exported import render_instance_properties;

uint ReadFragmentMaterialAddress(ByteAddressBuffer pageHeap, StructuredBuffer<VisibleCluster> visibleClusters,
    ByteAddressBuffer instanceData, uint materialOffsets, uint identity)
{
    DrawRequest source = VisibleClusterRequest(visibleClusters[DecodeVisBufferVisibleClusterIndex(identity)]);
    uint cluster = source.PageOffset + pageHeap.Load(source.PageOffset + 16u) +
        DrawRequestClusterID(source) * PACKED_CLUSTER_STRIDE_BYTES;
    uint material = LocalMaterial(pageHeap, cluster, source.PageOffset, DecodeVisBufferTriangleID(identity));
    uint offset = RenderInstanceLoad<uint>(instanceData, materialOffsets, source.InstanceID, 4u);
    return offset + material;
}
