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
    // bounded by MaxCandidates * 255 material batches; unit-work sums fit a wave.
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

// Cull contributes one item per active lane. The atomic read stops later window
// iterations at saturation; between reads and completed overflow clamps, at most
// MaxCandidates lanes contribute. capacity + MaxCandidates <= 2 * 64 * 65535.
uint ReserveClusterVisibleWork(RWByteAddressBuffer counter, uint offset, uint amount, uint capacity)
{
    uint current;
    counter.InterlockedAdd(offset, 0u, current);
    if (current >= capacity)
    {
        RejectClusterWork(8u);
        return capacity;
    }

    uint first;
    counter.InterlockedAdd(offset, amount, first);
    if (amount > capacity - min(first, capacity))
    {
        uint ignored;
        counter.InterlockedMin(offset, capacity, ignored);
        RejectClusterWork(8u);
    }
    return min(first, capacity);
}

// Each accepted visible item expands into at most 255 batches: every batch
// covers at least one triangle, and the cluster triangle count occupies 8 bits.
// The complete bin sum is <= 64 * 65535 * 255 = 1069531200, including rejections.
uint ReserveClusterRasterBatches(RWStructuredBuffer<uint> counter, uint amount, uint capacity)
{
    uint first;
    InterlockedAdd(counter[0], amount, first);
    if (amount > capacity - min(first, capacity))
    {
        uint ignored;
        InterlockedMin(counter[0], capacity, ignored);
        RejectClusterWork(16u);
    }
    return min(first, capacity);
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
