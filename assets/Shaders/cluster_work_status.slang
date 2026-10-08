#ifndef CLUSTER_WORK_STATUS_SLANG
#define CLUSTER_WORK_STATUS_SLANG
#include "resource_effects.slang"

// Match ClusterWorkOverflow. The status lives for the whole frame, across both
// primary and target main/post phases. Clamping protects memory, never quality.
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer WorkStatus;

// Optional completed-frame diagnostics share the failure holder. Paired uint
// atomics retain 64-bit totals without requiring buffer-int64 atomic capability.
// No consumer reads them until the GPU frame has completed.
void AddClusterWorkMetric(uint offset, uint count)
{
    uint bytes;
    WorkStatus.GetDimensions(bytes);
    if (bytes < 96u) return;
    // Callers use one counter offset in each branch. Accepted bin totals are
    // bounded by MaxCandidates * nine material batches; unit-work sums fit a wave.
    uint waveCount = WaveActiveSum(count);
    if (!WaveIsFirstLane() || waveCount == 0u) return;
    uint previous;
    WorkStatus.InterlockedAdd(offset, waveCount, previous);
    if (previous > 0xFFFFFFFFu - waveCount)
    {
        uint ignored;
        WorkStatus.InterlockedAdd(offset + 4u, 1u, ignored);
    }
}

void RejectClusterWork(uint kind)
{
    uint ignored;
    WorkStatus.InterlockedOr(0u, kind, ignored);
}

// Keep all queue counts in [0, capacity]. Even an arbitrarily large rejected
// stream cannot wrap a uint count back into a writable part of the queue.
uint ReserveClusterWork(RWStructuredBuffer<uint> counter, uint amount, uint capacity, uint kind)
{
    uint current = counter[0];
    for (;;)
    {
        uint admitted = min(amount, capacity - min(current, capacity));
        uint observed;
        InterlockedCompareExchange(counter[0], current, current + admitted, observed);
        if (observed == current)
        {
            if (admitted != amount) RejectClusterWork(kind);
            return current;
        }
        current = observed;
    }
}

uint ReserveClusterWork(RWByteAddressBuffer counter, uint offset, uint amount, uint capacity, uint kind)
{
    uint current = counter.Load(offset);
    for (;;)
    {
        uint admitted = min(amount, capacity - min(current, capacity));
        uint observed;
        counter.InterlockedCompareExchange(offset, current, current + admitted, observed);
        if (observed == current)
        {
            if (admitted != amount) RejectClusterWork(kind);
            return current;
        }
        current = observed;
    }
}
#endif
