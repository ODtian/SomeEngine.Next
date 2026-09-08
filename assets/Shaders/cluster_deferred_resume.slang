#include "cluster_structures.slang"
#include "resource_effects.slang"
#include "wave_aggregation.slang"

struct ClusterDeferredResumeUniforms
{
    uint MaxWorkItems;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<ClusterDeferredResumeUniforms> ResumeUniforms;
StructuredBuffer<DrawRequest> DeferredWork;
StructuredBuffer<uint> DeferredWorkCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterTraversalNode> TraversalNodes;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TraversalArguments;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DrawRequest> CandidateClusters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> CandidateCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer CandidateArgs;

void PublishDispatch(
    RWByteAddressBuffer arguments,
    uint count,
    uint parentsPerGroup)
{
    uint groups = (count + parentsPerGroup - 1u) / parentsPerGroup;
    uint ignored;
    arguments.InterlockedMax(0u, groups, ignored);
    arguments.Store(4u, 1u);
    arguments.Store(8u, 1u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ResumeDeferredClusterWork(uint3 threadId : SV_DispatchThreadID)
{
    uint count = min(DeferredWorkCount[0], ResumeUniforms.MaxWorkItems);
    bool valid = threadId.x < count;
    DrawRequest work = valid ? DeferredWork[threadId.x] : (DrawRequest)0;
    bool isNode = valid && IsDeferredTraversalNode(work);
    bool isCluster = valid && !isNode;

    uint4 nodeMask = WaveActiveBallot(isNode);
    if (isNode)
    {
        uint lane = WaveGetLaneIndex();
        uint leader = WaveMaskFirstLane(nodeMask);
        uint waveBase = 0u;
        uint waveCount = WaveMaskBitCount(nodeMask);
        if (lane == leader)
        {
            TraversalArguments.InterlockedAdd(12u, waveCount, waveBase);
            uint published = min(waveBase + waveCount, ResumeUniforms.MaxWorkItems);
            PublishDispatch(TraversalArguments, published, 4u);
            TraversalArguments.Store(16u, 0u);
            TraversalArguments.Store(20u, 0u);
            TraversalArguments.Store(24u, published);
        }
        waveBase = WaveReadLaneAt(waveBase, leader);
        uint output = waveBase + WaveMaskPrefix(nodeMask, lane);
        if (output < ResumeUniforms.MaxWorkItems)
            TraversalNodes[output] = DecodeDeferredTraversalNode(work);
    }

    uint4 clusterMask = WaveActiveBallot(isCluster);
    if (isCluster)
    {
        uint lane = WaveGetLaneIndex();
        uint leader = WaveMaskFirstLane(clusterMask);
        uint waveBase = 0u;
        uint waveCount = WaveMaskBitCount(clusterMask);
        if (lane == leader)
        {
            InterlockedAdd(CandidateCount[0], waveCount, waveBase);
            uint published = min(waveBase + waveCount, ResumeUniforms.MaxWorkItems);
            PublishDispatch(CandidateArgs, published, 64u);
        }
        waveBase = WaveReadLaneAt(waveBase, leader);
        uint output = waveBase + WaveMaskPrefix(clusterMask, lane);
        if (output < ResumeUniforms.MaxWorkItems)
            CandidateClusters[output] = work;
    }
}
