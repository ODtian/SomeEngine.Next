namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphSnapshotDiff
{
    public static IReadOnlyList<string> Compare(
        RenderGraphSnapshot before,
        RenderGraphSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var differences = new List<string>();
        CompareValue("version", before.Version, after.Version, differences);
        CompareValue("structureVersion", before.StructureVersion, after.StructureVersion, differences);
        CompareRows("passes", before.Passes, after.Passes, differences);
        CompareRows("buffers", before.Buffers, after.Buffers, differences);
        CompareRows("textures", before.Textures, after.Textures, differences);
        CompareRows("accesses", before.Accesses, after.Accesses, differences);
        CompareRows("dependencies", before.Dependencies, after.Dependencies, differences);
        CompareRows("barriers", before.Barriers, after.Barriers, differences);
        CompareValue("statistics", before.Statistics, after.Statistics, differences);
        return differences;
    }

    private static void CompareRows<T>(
        string name,
        IReadOnlyList<T> before,
        IReadOnlyList<T> after,
        List<string> differences)
    {
        if (before.Count != after.Count)
            differences.Add($"{name}.count: {before.Count} != {after.Count}");
        int count = Math.Min(before.Count, after.Count);
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int index = 0; index < count; index++)
        {
            if (!comparer.Equals(before[index], after[index]))
                differences.Add($"{name}[{index}]: {before[index]} != {after[index]}");
        }
    }

    private static void CompareValue<T>(
        string name,
        T before,
        T after,
        List<string> differences)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
            differences.Add($"{name}: {before} != {after}");
    }
}
