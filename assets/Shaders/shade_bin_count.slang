#include "shade_bin_common.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> BinCounts;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2D<uint> VisBuffer;
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint> SlotBuffer;
ByteAddressBuffer PageHeap;

[shader("compute")]
[numthreads(8, 8, 1)]
void CountShadeBins(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x >= Uniforms.ScreenWidth || threadId.y >= Uniforms.ScreenHeight)
        return;

    uint visibility = VisBuffer[threadId.xy];
    if (IsVisBufferEmpty(visibility))
        return;

    uint4 cluster = VisibleClusters[DecodeVisBufferVisibleClusterIndex(visibility)];
    uint slotOffset = MaterialSlotOffset(InstanceData, InstanceProperties, cluster.z);
    uint bin = FindShadeBin(
        SlotBuffer,
        PageHeap,
        Uniforms,
        slotOffset,
        cluster,
        DecodeVisBufferTriangleID(visibility));
    CountShade(BinCounts, bin);
}
