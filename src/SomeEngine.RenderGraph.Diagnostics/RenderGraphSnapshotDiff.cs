namespace SomeEngine.RenderGraph.Diagnostics;

using System.Collections.Immutable;

public static class RenderGraphSnapshotDiff
{
    public static ImmutableArray<string> Compare(RenderGraphSnapshot before, RenderGraphSnapshot after)
        => Compare(before, after, compareQueuePositionValues: true);

    public static ImmutableArray<string> Compare(
        RenderGraphSnapshot before,
        RenderGraphSnapshot after,
        bool compareQueuePositionValues)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        List<string> differences = [];
        if (before.Version != after.Version)
            differences.Add($"version: {before.Version} -> {after.Version}");
        if (before.Succeeded != after.Succeeded)
            differences.Add($"succeeded: {before.Succeeded} -> {after.Succeeded}");
        CompareCount("resources", before.Resources.Length, after.Resources.Length, differences);
        CompareCount("bufferViews", before.BufferViews.Length, after.BufferViews.Length, differences);
        CompareCount("textureViews", before.TextureViews.Length, after.TextureViews.Length, differences);
        CompareCount(
            "accelerationStructures",
            before.AccelerationStructures.Length,
            after.AccelerationStructures.Length,
            differences);
        CompareCount("passes", before.Passes.Length, after.Passes.Length, differences);
        CompareCount("accesses", before.Accesses.Length, after.Accesses.Length, differences);
        CompareCount(
            "shaderArguments",
            before.ShaderArguments.Length,
            after.ShaderArguments.Length,
            differences);
        CompareCount(
            "dependencies",
            before.Dependencies.Length,
            after.Dependencies.Length,
            differences);
        CompareCount("barriers", before.Barriers.Length, after.Barriers.Length, differences);
        CompareCount("units", before.Units.Length, after.Units.Length, differences);
        CompareCount("tasks", before.Tasks.Length, after.Tasks.Length, differences);
        CompareCount("batches", before.Batches.Length, after.Batches.Length, differences);
        CompareCount("timings", before.Timings.Length, after.Timings.Length, differences);

        int passCount = Math.Min(before.Passes.Length, after.Passes.Length);
        for (int index = 0; index < passCount; index++)
            ComparePass(index, before.Passes[index], after.Passes[index], differences);
        int resourceCount = Math.Min(before.Resources.Length, after.Resources.Length);
        for (int index = 0; index < resourceCount; index++)
            if (before.Resources[index] != after.Resources[index])
                differences.Add($"resource[{index}] changed.");
        CompareRows("bufferViews", before.BufferViews, after.BufferViews, differences);
        CompareRows("textureViews", before.TextureViews, after.TextureViews, differences);
        CompareRows(
            "accelerationStructures",
            before.AccelerationStructures,
            after.AccelerationStructures,
            differences);
        int accessCount = Math.Min(before.Accesses.Length, after.Accesses.Length);
        for (int index = 0; index < accessCount; index++)
            if (before.Accesses[index] != after.Accesses[index])
                differences.Add($"access[{index}] changed.");
        CompareRows(
            "shaderArguments",
            before.ShaderArguments,
            after.ShaderArguments,
            differences);
        CompareRows(
            "dependencies",
            before.Dependencies,
            after.Dependencies,
            differences);
        int barrierCount = Math.Min(before.Barriers.Length, after.Barriers.Length);
        for (int index = 0; index < barrierCount; index++)
            if (before.Barriers[index] != after.Barriers[index])
                differences.Add($"barrier[{index}] changed.");
        int unitCount = Math.Min(before.Units.Length, after.Units.Length);
        for (int index = 0; index < unitCount; index++)
            CompareUnit(index, before.Units[index], after.Units[index], differences);
        int taskCount = Math.Min(before.Tasks.Length, after.Tasks.Length);
        for (int index = 0; index < taskCount; index++)
            CompareTask(index, before.Tasks[index], after.Tasks[index], differences);
        int batchCount = Math.Min(before.Batches.Length, after.Batches.Length);
        for (int index = 0; index < batchCount; index++)
        {
            CompareBatch(
                index,
                before.Batches[index],
                after.Batches[index],
                compareQueuePositionValues,
                differences);
        }
        int timingCount = Math.Min(before.Timings.Length, after.Timings.Length);
        for (int index = 0; index < timingCount; index++)
        {
            RenderGraphSnapshot.Timing left = before.Timings[index];
            RenderGraphSnapshot.Timing right = after.Timings[index];
            if (left.Name != right.Name ||
                left.ClockDomain != right.ClockDomain ||
                left.Unit != right.Unit)
            {
                differences.Add($"timing[{index}] identity changed.");
            }
        }
        return [.. differences];
    }

    private static void ComparePass(
        int index,
        in RenderGraphSnapshot.Pass before,
        in RenderGraphSnapshot.Pass after,
        List<string> differences)
    {
        if (before.Ordinal != after.Ordinal ||
            before.ExecutionOrdinal != after.ExecutionOrdinal ||
            before.Name != after.Name ||
            before.Queue != after.Queue ||
            before.Flags != after.Flags ||
            before.Live != after.Live ||
            before.Root != after.Root ||
            before.AccessOffset != after.AccessOffset ||
            before.AccessCount != after.AccessCount ||
            before.ShaderArgumentOffset != after.ShaderArgumentOffset ||
            before.ShaderArgumentCount != after.ShaderArgumentCount ||
            before.DependencyOffset != after.DependencyOffset ||
            before.DependencyCount != after.DependencyCount)
        {
            differences.Add($"pass[{index}] changed.");
        }
    }

    private static void CompareUnit(
        int index,
        in RenderGraphSnapshot.Command before,
        in RenderGraphSnapshot.Command after,
        List<string> differences)
    {
        if (before.Ordinal != after.Ordinal ||
            before.Name != after.Name ||
            before.Queue != after.Queue ||
            before.AliasBarrierCount != after.AliasBarrierCount ||
            before.BarrierCount != after.BarrierCount)
        {
            differences.Add($"unit[{index}] changed.");
        }
        CompareRows(
            $"unit[{index}].passes",
            before.PassOrdinals,
            after.PassOrdinals,
            differences);
        CompareRows(
            $"unit[{index}].dependencies",
            before.Dependencies,
            after.Dependencies,
            differences);
    }

    private static void CompareTask(
        int index,
        in RenderGraphSnapshot.Task before,
        in RenderGraphSnapshot.Task after,
        List<string> differences)
    {
        if (before.Ordinal != after.Ordinal ||
            before.Queue != after.Queue ||
            before.RequiresCoordinator != after.RequiresCoordinator ||
            before.Exclusive != after.Exclusive ||
            before.BarrierCount != after.BarrierCount)
        {
            differences.Add($"task[{index}] changed.");
        }
        CompareRows(
            $"task[{index}].units",
            before.UnitOrdinals,
            after.UnitOrdinals,
            differences);
    }

    private static void CompareBatch(
        int index,
        in RenderGraphSnapshot.Batch before,
        in RenderGraphSnapshot.Batch after,
        bool compareQueuePositionValues,
        List<string> differences)
    {
        if (before.Ordinal != after.Ordinal ||
            before.Queue != after.Queue ||
            !QueuePositionEqual(before.Position, after.Position, compareQueuePositionValues))
        {
            differences.Add($"batch[{index}] changed.");
        }
        CompareRows(
            $"batch[{index}].dependencies",
            before.Dependencies,
            after.Dependencies,
            differences);
        CompareRows(
            $"batch[{index}].units",
            before.UnitOrdinals,
            after.UnitOrdinals,
            differences);
        CompareRows(
            $"batch[{index}].tasks",
            before.TaskOrdinals,
            after.TaskOrdinals,
            differences);
        CompareQueuePositions(
            $"batch[{index}].externalWaits",
            before.ExternalWaits,
            after.ExternalWaits,
            compareQueuePositionValues,
            differences);
    }

    private static bool QueuePositionEqual(
        RenderGraphSnapshot.Fence? before,
        RenderGraphSnapshot.Fence? after,
        bool compareValues)
    {
        if (!before.HasValue || !after.HasValue)
            return before.HasValue == after.HasValue;
        return before.Value.Queue == after.Value.Queue &&
            (!compareValues || before.Value.Value == after.Value.Value);
    }

    private static void CompareQueuePositions(
        string name,
        ImmutableArray<RenderGraphSnapshot.Fence> before,
        ImmutableArray<RenderGraphSnapshot.Fence> after,
        bool compareValues,
        List<string> differences)
    {
        if (before.Length != after.Length)
        {
            differences.Add($"{name} changed.");
            return;
        }
        for (int index = 0; index < before.Length; index++)
        {
            if (before[index].Queue != after[index].Queue ||
                compareValues && before[index].Value != after[index].Value)
            {
                differences.Add($"{name} changed.");
                return;
            }
        }
    }

    private static void CompareRows<T>(
        string name,
        ImmutableArray<T> before,
        ImmutableArray<T> after,
        List<string> differences)
        where T : IEquatable<T>
    {
        if (before.AsSpan().SequenceEqual(after.AsSpan()))
            return;
        differences.Add($"{name} changed.");
    }

    private static void CompareCount(string name, int before, int after, List<string> differences)
    {
        if (before != after) differences.Add($"{name}: {before} -> {after}");
    }
}
