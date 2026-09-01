#include "shade_bin_common.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> BinCounts;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2DArray<uint> VisBuffer;
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint> ShadeSlots;
ByteAddressBuffer PageHeap;

[shader("compute")]
[numthreads(8, 8, 1)]
void CountShadeBins(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x >= Uniforms.ScreenWidth || threadId.y >= Uniforms.ScreenHeight)
        return;

    uint viewIndex = threadId.z;
    uint visibility = VisBuffer[uint3(threadId.xy, viewIndex)];
    if (IsVisBufferEmpty(visibility))
        return;

    uint4 cluster = VisibleClusters[DecodeVisBufferVisibleClusterIndex(visibility)];
    if (cluster.w != viewIndex)
        return;
    uint slotOffset = MaterialSlotOffset(InstanceData, InstanceProperties, cluster.z);
    uint bin = FindShadeBin(
        ShadeSlots,
        PageHeap,
        Uniforms,
        slotOffset,
        cluster,
        DecodeVisBufferTriangleID(visibility));
    CountShade(BinCounts, bin);
}
