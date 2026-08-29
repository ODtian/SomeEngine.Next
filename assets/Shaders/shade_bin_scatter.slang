#include "shade_bin_common.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2D<uint> VisBuffer;
[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinOffsets;
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint> SlotBuffer;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ScatterCounts;
ByteAddressBuffer PageHeap;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> PixelCoordinates;

[shader("compute")]
[numthreads(8, 8, 1)]
void ScatterShadeBins(uint3 threadId : SV_DispatchThreadID)
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
    ScatterShade(
        PixelCoordinates,
        BinOffsets,
        ScatterCounts,
        bin,
        (threadId.y << 16u) | threadId.x);
}
