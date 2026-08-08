#include "cluster_structures.slang"
#include "common.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view.slang"
#include "resource_effects.slang"

ConstantBuffer<ClusterViewUniforms> Uniforms;

StructuredBuffer<ClusterBVHNode> GlobalBVH;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer CandidateArgs;           // IndirectArgs: must stay raw
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint3> CandidateClusters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> CandidateCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer PageFaultBuffer;

// Mesh registration proves that the exact depth-first pending set fits this array. Overflow is a
// rejected asset invariant, never a runtime branch that silently drops a subtree.
static const uint TraverseStackCapacity = 128;

void PublishCandidateDispatchArgs(uint start, uint count)
{
    if (count == 0 || start >= Uniforms.MaxCandidates)
        return;

    uint clampedCount = min(count, Uniforms.MaxCandidates - start);
    uint clampedEnd = start + clampedCount;
    uint groups = (clampedEnd + 63) / 64;
    uint unused;
    CandidateArgs.InterlockedMax(0, groups, unused);
}

float LocalExpansionForWorldRadius(RenderInstanceTransform t, float worldExpansion)
{
    if (worldExpansion <= 0.0)
        return 0.0;

    float3 axisScale = abs(t.Stretch) * abs(t.Scale);
    float minAxisScale = max(min(min(axisScale.x, axisScale.y), axisScale.z), 1e-6);
    return worldExpansion / minAxisScale;
}

bool IsNodeOutsideFrustum(
    ClusterBVHNode node,
    RenderInstanceTransform t,
    float boundsExpansion)
{
    float3 localMin = node.BoundMin.xyz;
    float3 localMax = node.BoundMax.xyz;
    float localExpansion = LocalExpansionForWorldRadius(t, boundsExpansion);
    float3 localExpansion3 = float3(localExpansion, localExpansion, localExpansion);
    localMin -= localExpansion3;
    localMax += localExpansion3;

    float3 corners[8];
    corners[0] = float3(localMin.x, localMin.y, localMin.z);
    corners[1] = float3(localMax.x, localMin.y, localMin.z);
    corners[2] = float3(localMin.x, localMax.y, localMin.z);
    corners[3] = float3(localMax.x, localMax.y, localMin.z);
    corners[4] = float3(localMin.x, localMin.y, localMax.z);
    corners[5] = float3(localMax.x, localMin.y, localMax.z);
    corners[6] = float3(localMin.x, localMax.y, localMax.z);
    corners[7] = float3(localMax.x, localMax.y, localMax.z);

    uint outsideLeft = 0;
    uint outsideRight = 0;
    uint outsideBottom = 0;
    uint outsideTop = 0;
    uint outsideNear = 0;
    uint outsideFar = 0;

    [unroll]
    for (uint i = 0; i < 8; ++i)
    {
        float3 world = TransformInstanceLocalPosition(corners[i], t);
        float4 clip = mul(float4(world, 1.0), Uniforms.ViewProj);

        outsideLeft += (clip.x < -clip.w) ? 1 : 0;
        outsideRight += (clip.x > clip.w) ? 1 : 0;
        outsideBottom += (clip.y < -clip.w) ? 1 : 0;
        outsideTop += (clip.y > clip.w) ? 1 : 0;
        outsideNear += (clip.z < 0.0) ? 1 : 0;
        outsideFar += (clip.z > clip.w) ? 1 : 0;
    }

    return outsideLeft == 8 || outsideRight == 8 || outsideBottom == 8 || outsideTop == 8 || outsideNear == 8 || outsideFar == 8;
}

[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint instanceID = dispatchThreadID.x;
    if (instanceID >= Uniforms.InstanceCount)
        return;

    uint bvhRootIndex = LoadClusterBvhRoot(
        InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BVH_ROOT_METADATA_WORD),
        instanceID);
    if (bvhRootIndex == 0xFFFFFFFF)
        return;

    uint nodeStack[TraverseStackCapacity];
    uint depthStack[TraverseStackCapacity];
    uint stackCount = 1;
    nodeStack[0] = bvhRootIndex;
    depthStack[0] = 0;

    while (stackCount > 0)
    {
        stackCount--;
        uint nodeIndex = nodeStack[stackCount];
        uint currentDepth = depthStack[stackCount];

        ClusterBVHNode node = GlobalBVH[nodeIndex];

        if (node.NodeType == 1 && node.ChildPointer == 0xFFFFFFFF)
        {
            uint faultIndex;
            PageFaultBuffer.InterlockedAdd(0, 1, faultIndex);
            if (faultIndex < Uniforms.MaxPageFaults)
            {
                PageFaultBuffer.Store(4 + faultIndex * 4, nodeIndex);
            }
            continue;
        }

        RenderInstanceTransform t = LoadRenderTransform(
            InstanceData,
            LoadRenderInstanceMetadata(InstanceProperties, RENDER_CURRENT_TRANSFORM_METADATA_WORD),
            instanceID);
        float boundsExpansion = max(
            0.0,
            LoadClusterBoundsExpansion(
                InstanceData,
                LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BOUNDS_EXPANSION_METADATA_WORD),
                instanceID));

        bool culled = IsNodeOutsideFrustum(node, t, boundsExpansion);

        float3 worldCenter = TransformInstanceLocalPosition(node.LODSphere.xyz, t);
        float maxScale = t.Scale * max(max(t.Stretch.x, t.Stretch.y), t.Stretch.z);
        float worldRadius = node.LODSphere.w * maxScale;

        float dist = distance(worldCenter, Uniforms.CameraPos);
        float distToSurface = max(0.0, dist - worldRadius);

        // LOD Culling
        if (node.LODError * Uniforms.LodScale <= Uniforms.LodThreshold * distToSurface)
        {
            culled = true;
        }

        if (culled)
            continue;

        if (node.NodeType == 0)
        {
            uint nextDepth = currentDepth + 1;
            if (nextDepth >= Uniforms.MaxTraversalDepth)
                continue;

            uint start = node.ChildPointer;
            uint count = node.ChildCount;
            for (uint i = 0; i < count; ++i)
            {
                nodeStack[stackCount] = start + i;
                depthStack[stackCount] = nextDepth;
                stackCount++;
            }
        }
        else
        {
            uint pageOffset = node.ChildPointer;
            uint start = node.ChildCount & 0xFFF;
            uint count = node.ChildCount >> 12;

            uint outIndex;
            InterlockedAdd(CandidateCount[0], count, outIndex);
            PublishCandidateDispatchArgs(outIndex, count);

            for (uint i = 0; i < count; ++i)
            {
                if (outIndex + i < Uniforms.MaxCandidates)
                {
                    uint clusterID = start + i;
                    CandidateClusters[outIndex + i] = uint3(pageOffset, clusterID, instanceID);
                }
            }
        }
    }
}
