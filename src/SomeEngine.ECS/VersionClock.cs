using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

internal static class VersionClock
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsNewer(uint current, uint last) => (int)(current - last) > 0;

    /// <summary>
    /// Publishes a coarse change version without allowing a later-finishing writer carrying an
    /// older tick to regress the shared value. Parallel packet owners write disjoint row-version
    /// slots, but packets from the same chunk still converge on one coarse change-version slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PublishNewest(ref uint target, uint candidate)
    {
        ref int targetBits = ref Unsafe.As<uint, int>(ref target);
        uint observed = unchecked((uint)Volatile.Read(ref targetBits));
        while (IsNewer(candidate, observed))
        {
            int priorBits = Interlocked.CompareExchange(
                ref targetBits,
                unchecked((int)candidate),
                unchecked((int)observed));
            uint prior = unchecked((uint)priorBits);
            if (prior == observed)
                return;

            observed = prior;
        }
    }
}

