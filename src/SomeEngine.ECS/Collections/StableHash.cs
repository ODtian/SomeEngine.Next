namespace SomeEngine.ECS.Collections;

/// <summary>
/// FNV-1a hash 工具类。用于对 sorted int[] 计算确定性 hash。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §4.2, §5.1
/// 标准 FNV-1a 32-bit：offset = 2166136261, prime = 16777619
/// </remarks>
internal static class StableHash
{
    private const uint OffsetBasis = 2166136261u;
    private const uint Prime = 16777619u;

    /// <summary>
    /// 对 sorted int span 计算 FNV-1a hash。
    /// </summary>
    public static uint Compute(ReadOnlySpan<int> ids)
    {
        uint hash = OffsetBasis;
        foreach (int id in ids)
        {
            // 逐字节 XOR + multiply（将 int 拆为 4 个字节）
            hash ^= (uint)(id & 0xFF);
            hash *= Prime;
            hash ^= (uint)((id >> 8) & 0xFF);
            hash *= Prime;
            hash ^= (uint)((id >> 16) & 0xFF);
            hash *= Prime;
            hash ^= (uint)((id >> 24) & 0xFF);
            hash *= Prime;
        }
        return hash;
    }
}

