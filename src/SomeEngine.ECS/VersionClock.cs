using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

internal static class VersionClock
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsNewer(uint current, uint last) => (int)(current - last) > 0;
}

