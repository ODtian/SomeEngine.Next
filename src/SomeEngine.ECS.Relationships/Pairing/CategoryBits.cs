using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

/// <summary>Category membership and allowed target categories in the same 64-bit finite domain.</summary>
public readonly struct CategoryBits(ulong categories, ulong allowedCategories)
{
    public readonly ulong Categories = categories;
    public readonly ulong AllowedCategories = allowedCategories;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Allows(ulong allowedCategories, ulong targetCategories) =>
        (allowedCategories & targetCategories) != 0;
}
