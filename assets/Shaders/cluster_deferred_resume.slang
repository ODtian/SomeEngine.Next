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
StructuredBuffer<ClusterCandidate> DeferredWork;
StructuredBuffer<uint> DeferredWorkCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterTraversalNode> TraversalNodes;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TraversalArguments;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterCandidate> CandidateClusters;
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
    arguments.InterlockedMax(0u, min(groups, 65535u), ignored);
    arguments.InterlockedMax(4u, (groups + 65534u) / 65535u, ignored);
    arguments.Store(8u, 1u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ResumeDeferredClusterWork(uint3 threadId : SV_DispatchThreadID)
{
    uint rawCount = DeferredWorkCount[0];
    if (rawCount > ResumeUniforms.MaxWorkItems) RejectClusterWork(4u);
    uint count = min(rawCount, ResumeUniforms.MaxWorkItems);
    bool valid = threadId.x < count;
    ClusterCandidate candidate = valid ? DeferredWork[threadId.x] : (ClusterCandidate)0;
    DrawRequest work = CandidateRequest(candidate);
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
            waveBase = ReserveClusterWork(TraversalArguments, 12u,
                waveCount, ResumeUniforms.MaxWorkItems, 1u);
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
            waveBase = ReserveClusterWork(CandidateCount, waveCount, ResumeUniforms.MaxWorkItems, 2u);
            uint published = min(waveBase + waveCount, ResumeUniforms.MaxWorkItems);
            PublishDispatch(CandidateArgs, published, 64u);
        }
        waveBase = WaveReadLaneAt(waveBase, leader);
        uint output = waveBase + WaveMaskPrefix(clusterMask, lane);
        if (output < ResumeUniforms.MaxWorkItems)
            CandidateClusters[output] = candidate;
    }
}

#include "cluster_work_status.slang"
