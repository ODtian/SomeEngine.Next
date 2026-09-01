#include "shade_bin_common.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2DArray<uint> VisBuffer;
[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinOffsets;
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint> ShadeSlots;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ScatterCounts;
ByteAddressBuffer PageHeap;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> PixelCoordinates;

[shader("compute")]
[numthreads(8, 8, 1)]
void ScatterShadeBins(uint3 threadId : SV_DispatchThreadID)
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
    ScatterShade(
        PixelCoordinates,
        BinOffsets,
        ScatterCounts,
        bin,
        (threadId.y << 16u) | threadId.x,
        viewIndex);
}
