#include "tile_visibility_common.slang"

[shader("compute")]
[numthreads(256, 1, 1)]
void ClearTileCoverage(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x < Settings.AssociationCount * TILE_MASK_WORDS) CoverageMasks[threadId.x] = 0;
}

