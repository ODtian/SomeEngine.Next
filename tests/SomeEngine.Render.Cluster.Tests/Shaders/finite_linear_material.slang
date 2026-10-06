#include "unlit.slang"

// This user-authored material keeps the original triangle positions everywhere.
// Its linear domain bounds are finite; source triangles remain independent truth.
#define CLUSTER_TESSELLATION_BOUNDS "finite"
#include "cluster_tessellation_entries.slang"
