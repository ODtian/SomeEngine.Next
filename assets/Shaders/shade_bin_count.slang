#include "shade_bin_common.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> BinCounts;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2DArray<uint> VisBuffer;
StructuredBuffer<DrawRequest> VisibleClusters;
StructuredBuffer<uint> ShadeSlots;
ByteAddressBuffer PageHeap;

[shader("compute")]
[numthreads(8, 8, 1)]
void CountShadeBins(uint3 threadId : SV_DispatchThreadID, uint lane : SV_GroupIndex)
{
    uint bin = 0xFFFFFFFFu;
    if (threadId.x < Uniforms.ScreenWidth && threadId.y < Uniforms.ScreenHeight)
    {
        uint visibility = VisBuffer[threadId];
        if (!IsVisBufferEmpty(visibility))
        {
            DrawRequest cluster = VisibleClusters[DecodeVisBufferVisibleClusterIndex(visibility)];
            if (DrawRequestViewIndex(cluster) == threadId.z)
            {
                uint slotOffset = MaterialSlotOffset(InstanceData, InstanceProperties, cluster.InstanceID);
                bin = FindShadeBin(ShadeSlots, PageHeap, Uniforms, slotOffset, cluster,
                    DecodeVisBufferTriangleID(visibility));
            }
        }
    }
    uint mask;
    bool fullTile;
    if (!ClassifyShadeTile(bin, lane, mask, fullTile)) return;
    uint counter = bin * 2u + (fullTile ? 0u : 1u);
    CountShade(BinCounts, counter);
}
