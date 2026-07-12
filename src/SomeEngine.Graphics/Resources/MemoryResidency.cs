namespace SomeEngine.Graphics;

public readonly record struct MemoryBudget(ulong Budget, ulong Usage, ulong Available)
{
    public static MemoryBudget FromUsage(ulong budget, ulong usage) =>
        new(budget, usage, usage >= budget ? 0 : budget - usage);
}

public enum ResidencyPriority : byte
{
    Minimum,
    Low,
    Normal,
    High,
    Critical,
}

public readonly record struct ResourceMemoryInfo(
    ResourceHandle Resource,
    MemoryType MemoryType,
    ulong Size,
    ulong Offset,
    ResidencyPriority Priority,
    bool Resident);
