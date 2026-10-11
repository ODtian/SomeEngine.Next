// wave_queue.slang — Wave-Level Generic Task Distribution
// Zero LDS, zero barriers. Pure register + wave intrinsics.
//
// Usage:
//   struct MyTask : IWaveTask { ... };
//   MyTask task = { ... };
//   WaveQueue::Distribute(task);

#ifndef WAVE_QUEUE_SLANG
#define WAVE_QUEUE_SLANG

/// Generic interface for wave-distributed tasks.
/// Each lane declares how many sub-tasks it produces via GetTaskCount().
/// The distributor maps consumer lanes to producer lanes and calls ExecuteTask().
interface IWaveTask
{
    /// Number of sub-tasks this lane produces.
    [mutating] uint GetTaskCount();

    /// Execute one sub-task.
    /// @param srcLane   The producer lane index. Use WaveReadLaneAt(field, srcLane) to pull data.
    /// @param localIdx  Which sub-task within the producer (0 .. GetTaskCount()-1).
    [mutating] void ExecuteTask(uint srcLane, uint localIdx, bool bActive);
};

/// Wave task whose execution also receives one bound uniform value. This is for
/// shader objects that can contain resource handles: the value remains a bound
/// parameter and is never copied into the per-lane task state.
interface IWaveUniformTask<TUniform>
{
    [mutating] uint GetTaskCount();
    [mutating] void ExecuteTask(
        uniform TUniform value,
        uint srcLane,
        uint localIdx,
        bool bActive);
};

struct WaveQueue
{
    // Find position of the n-th set bit (0-indexed) in a 32-bit mask.
    // Binary search from MSB, O(log2(32)) = 5 steps.
    static uint FindNthSetBit(uint mask, int n)
    {
        int last = int(countbits(mask)) - n - 1;
        uint p = 16u;
        p += (countbits(mask >> p) <= uint(last)) ? -8u : 8u;
        p += (countbits(mask >> p) <= uint(last)) ? -4u : 4u;
        p += (countbits(mask >> p) <= uint(last)) ? -2u : 2u;
        p += (countbits(mask >> p) <= uint(last)) ? -1u : 1u;
        p  = (countbits(mask >> p) == uint(last)) ? (p - 1u) : p;
        return p;
    }

    static uint MaskBitCount(uint4 mask)
    {
        return countbits(mask.x) + countbits(mask.y) +
            countbits(mask.z) + countbits(mask.w);
    }

    static uint MaskPrefixBitCount(uint4 mask, uint bit)
    {
        uint word = bit >> 5u;
        uint lane = bit & 31u;
        uint result = 0u;
        if (word > 0u) result += countbits(mask.x);
        if (word > 1u) result += countbits(mask.y);
        if (word > 2u) result += countbits(mask.z);
        uint current = word == 0u ? mask.x :
            word == 1u ? mask.y :
            word == 2u ? mask.z : mask.w;
        uint lower = lane == 0u ? 0u : (1u << lane) - 1u;
        return result + countbits(current & lower);
    }

    static uint FindNthSetBit(uint4 mask, uint index)
    {
        uint xCount = countbits(mask.x);
        if (index < xCount)
            return FindNthSetBit(mask.x, int(index));
        index -= xCount;
        uint yCount = countbits(mask.y);
        if (index < yCount)
            return 32u + FindNthSetBit(mask.y, int(index));
        index -= yCount;
        uint zCount = countbits(mask.z);
        if (index < zCount)
            return 64u + FindNthSetBit(mask.z, int(index));
        return 96u + FindNthSetBit(mask.w, int(index - zCount));
    }

    static uint4 OneBit(uint bit)
    {
        uint4 result = 0u.xxxx;
        result[bit >> 5u] = 1u << (bit & 31u);
        return result;
    }

    /// Distribute sub-tasks across all lanes in the current wave.
    /// Handles empty producers (myCount==0) via compact index mapping.
    static void Distribute<T : IWaveTask>(inout T task)
    {
        uint myCount  = task.GetTaskCount();
        uint myOffset = WavePrefixSum(myCount);
        uint totalTasks = WaveActiveSum(myCount);

        if (totalTasks == 0)
            return;

        uint laneIdx = WavePrefixSum(1u);
        uint activeLaneCount = WaveActiveSum(1u);

        uint4 nonEmptyMask = WaveActiveBallot(myCount > 0);
        uint numProducers = MaskBitCount(nonEmptyMask);
        uint maxCompact   = numProducers > 0 ? numProducers - 1 : 0;

        uint compactHead = 0;
        uint workHead    = 0;

        while (workHead < totalTasks)
        {
            // Each producer marks its last output slot in this batch
            uint4 boundaryMask = 0u.xxxx;
            if (myCount > 0)
            {
                int lastSlot = int(myOffset + myCount - 1) - int(workHead);
                if (lastSlot >= 0 && uint(lastSlot) < activeLaneCount)
                    boundaryMask = OneBit(uint(lastSlot));
            }
            uint4 boundaryBits = uint4(
                WaveActiveBitOr(boundaryMask.x),
                WaveActiveBitOr(boundaryMask.y),
                WaveActiveBitOr(boundaryMask.z),
                WaveActiveBitOr(boundaryMask.w));

            // Consumer: find which producer owns this task
            uint taskIdx    = workHead + laneIdx;
            uint prodOffset = MaskPrefixBitCount(boundaryBits, laneIdx);
            uint compactIdx = compactHead + prodOffset;
            uint srcLane = FindNthSetBit(nonEmptyMask, min(compactIdx, maxCompact));

            uint srcStart = WaveReadLaneAt(myOffset, srcLane);
            uint localIdx = taskIdx - srcStart;

            // Execute unconditionally — WaveReadLaneAt inside ExecuteTask
            // requires all lanes active to avoid UNDEFINED reads.
            task.ExecuteTask(srcLane, localIdx, taskIdx < totalTasks);

            workHead += activeLaneCount;
            compactHead += MaskBitCount(boundaryBits);
        }
    }

    /// Same distribution as Distribute(), but passes a bound uniform object to
    /// task execution instead of requiring it to be copied into task state.
    static void DistributeWithUniform<
        TUniform,
        T : IWaveUniformTask<TUniform>>(
        inout T task,
        uniform TUniform value)
    {
        uint myCount = task.GetTaskCount();
        uint myOffset = WavePrefixSum(myCount);
        uint4 nonEmptyMask = WaveActiveBallot(myCount > 0);
        uint numProducers = MaskBitCount(nonEmptyMask);
        if (numProducers == 0u)
            return;

        // The last producer's inclusive prefix is the complete wave total,
        // including sparse active lanes. Reuse the scan instead of reducing twice.
        uint lastProducer = nonEmptyMask.w != 0u ? 96u + firstbithigh(nonEmptyMask.w) :
            nonEmptyMask.z != 0u ? 64u + firstbithigh(nonEmptyMask.z) :
            nonEmptyMask.y != 0u ? 32u + firstbithigh(nonEmptyMask.y) :
            firstbithigh(nonEmptyMask.x);
        uint totalTasks = WaveReadLaneAt(myOffset + myCount, lastProducer);
        if (totalTasks == 0u)
            return;

        uint laneIdx = WavePrefixSum(1u);
        uint activeLaneCount = WaveActiveSum(1u);
        uint maxCompact = numProducers - 1u;

        uint compactHead = 0;
        uint workHead = 0;

        while (workHead < totalTasks)
        {
            uint4 boundaryMask = 0u.xxxx;
            if (myCount > 0)
            {
                int lastSlot = int(myOffset + myCount - 1) - int(workHead);
                if (lastSlot >= 0 && uint(lastSlot) < activeLaneCount)
                    boundaryMask = OneBit(uint(lastSlot));
            }
            uint4 boundaryBits = uint4(
                WaveActiveBitOr(boundaryMask.x),
                WaveActiveBitOr(boundaryMask.y),
                WaveActiveBitOr(boundaryMask.z),
                WaveActiveBitOr(boundaryMask.w));

            uint taskIdx = workHead + laneIdx;
            uint prodOffset = MaskPrefixBitCount(boundaryBits, laneIdx);
            uint compactIdx = compactHead + prodOffset;
            uint srcLane = FindNthSetBit(nonEmptyMask, min(compactIdx, maxCompact));

            uint srcStart = WaveReadLaneAt(myOffset, srcLane);
            uint localIdx = taskIdx - srcStart;

            task.ExecuteTask(value, srcLane, localIdx, taskIdx < totalTasks);

            workHead += activeLaneCount;
            compactHead += MaskBitCount(boundaryBits);
        }
    }
};

#endif // WAVE_QUEUE_SLANG
