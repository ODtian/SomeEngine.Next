#ifndef CLUSTER_WORK_QUEUE_SLANG
#define CLUSTER_WORK_QUEUE_SLANG
#include "resource_effects.slang"
#include "cluster_work_status.slang"

// One count holder: dispatch XYZ, node write/read/outstanding, cluster batch read/count.
static const uint CLUSTER_NODE_WRITE = 12u;
static const uint CLUSTER_NODE_READ = 16u;
static const uint CLUSTER_NODE_OUTSTANDING = 24u;
static const uint CLUSTER_BATCH_READ = 20u;
static const uint CLUSTER_BATCH_COUNT = 28u;
static const uint CLUSTER_BATCH_SIZE = 64u;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
globallycoherent RWStructuredBuffer<uint> TraversalReady;
#endif
