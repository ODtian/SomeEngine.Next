#ifndef CLUSTER_OVERFLOW_SLANG
#define CLUSTER_OVERFLOW_SLANG

// Keep synchronized with ClusterOverflowCounter.cs.
static const uint CLUSTER_OVERFLOW_TRAVERSAL_CANDIDATES = 0u;
static const uint CLUSTER_OVERFLOW_PHASE_ONE_VISIBLE = 1u;
static const uint CLUSTER_OVERFLOW_PHASE_TWO_CANDIDATES = 2u;
static const uint CLUSTER_OVERFLOW_PHASE_TWO_VISIBLE = 3u;
static const uint CLUSTER_OVERFLOW_SHADOW_CANDIDATES = 4u;
static const uint CLUSTER_OVERFLOW_LIGHT_GRID = 5u;
static const uint CLUSTER_OVERFLOW_VSM_REQUESTS = 6u;
static const uint CLUSTER_OVERFLOW_VSM_RASTER_BUDGET = 7u;
static const uint CLUSTER_OVERFLOW_COUNTER_COUNT = 8u;

#endif
