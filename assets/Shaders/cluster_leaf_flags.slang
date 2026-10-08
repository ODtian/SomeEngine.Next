struct ClusterLeafFlagFixup
{
    uint ClusterByteAddress;
    uint StreamingLeaf;
};
struct LeafFlagParameters { uint Count; uint3 Padding; };
ConstantBuffer<LeafFlagParameters> Parameters;
StructuredBuffer<ClusterLeafFlagFixup> Fixups;
RWByteAddressBuffer PageHeap;

[numthreads(64, 1, 1)]
void ApplyClusterLeafFlags(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= Parameters.Count) return;
    ClusterLeafFlagFixup fixup = Fixups[thread.x];
    uint previous;
    if (fixup.StreamingLeaf != 0u)
        PageHeap.InterlockedOr(fixup.ClusterByteAddress + 60u, 2u << 24u, previous);
    else
        PageHeap.InterlockedAnd(fixup.ClusterByteAddress + 60u, ~(2u << 24u), previous);
}
