#ifndef WAVE_AGGREGATION_SLANG
#define WAVE_AGGREGATION_SLANG

uint WaveMaskBitCount(uint4 mask)
{
    return countbits(mask.x) + countbits(mask.y) + countbits(mask.z) + countbits(mask.w);
}

uint WaveMaskFirstLane(uint4 mask)
{
    if (mask.x != 0u) return firstbitlow(mask.x);
    if (mask.y != 0u) return 32u + firstbitlow(mask.y);
    if (mask.z != 0u) return 64u + firstbitlow(mask.z);
    return 96u + firstbitlow(mask.w);
}

uint WaveMaskPrefix(uint4 mask, uint lane)
{
    uint word = lane >> 5u;
    uint bit = lane & 31u;
    uint prefix = 0u;
    if (word > 0u) prefix += countbits(mask.x);
    if (word > 1u) prefix += countbits(mask.y);
    if (word > 2u) prefix += countbits(mask.z);
    uint current = word == 0u ? mask.x : word == 1u ? mask.y : word == 2u ? mask.z : mask.w;
    uint lower = bit == 0u ? 0u : ((1u << bit) - 1u);
    return prefix + countbits(current & lower);
}

uint4 WaveMatchingLanes(uint value)
{
    __target_switch
    {
    case _sm_6_5:
        return WaveMatch(value);
    case hlsl:
    default:
        // Partition only the varying key bits. This takes at most 32 ballots,
        // and only six for a Cluster's 64 local vertices, independent of the
        // number of distinct keys or the device's subgroup width.
        uint4 own = WaveActiveBallot(true);
        uint varyingBits = WaveActiveBitOr(value ^ WaveReadLaneFirst(value));
        while (varyingBits != 0u)
        {
            uint bit = 1u << firstbitlow(varyingBits);
            varyingBits &= varyingBits - 1u;
            bool set = (value & bit) != 0u;
            uint4 matching = WaveActiveBallot(set);
            own &= set ? matching : ~matching;
        }
        return own;
    }
}

#endif
