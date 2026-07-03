namespace SomeEngine.ECS.Systems;

public sealed class SystemSchedule
{
    internal SystemSchedule(SystemScheduleStage[] stages)
    {
        Stages = Array.AsReadOnly(stages);
    }

    public IReadOnlyList<SystemScheduleStage> Stages { get; }
}

public sealed class SystemScheduleStage
{
    private readonly int[] _systemIndices;

    internal SystemScheduleStage(int[] systemIndices, bool requiresBarrierAfter)
    {
        _systemIndices = systemIndices;
        SystemIndices = Array.AsReadOnly(_systemIndices);
        RequiresBarrierAfter = requiresBarrierAfter;
    }

    public IReadOnlyList<int> SystemIndices { get; }

    public bool RequiresBarrierAfter { get; }
}

public static class SystemScheduleBuilder
{
    public static SystemSchedule Build(
        IReadOnlyList<SystemAccessManifest> manifests,
        IReadOnlyList<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        if (enabled != null && enabled.Count != manifests.Count)
            throw new ArgumentException("Enabled mask length must match manifest count.", nameof(enabled));

        var stages = new List<SystemScheduleStage>();
        var currentIndices = new List<int>();
        var currentManifests = new List<SystemAccessManifest>();
        bool currentRequiresBarrierAfter = false;

        for (int i = 0; i < manifests.Count; i++)
        {
            if (enabled != null && !enabled[i])
                continue;

            var manifest = manifests[i] ?? throw new ArgumentException(
                $"Manifest at index {i} is null.",
                nameof(manifests));

            if (manifest.RequiresExclusiveStage)
            {
                FlushCurrentStage(stages, currentIndices, currentManifests, ref currentRequiresBarrierAfter);
                stages.Add(new SystemScheduleStage(new[] { i }, requiresBarrierAfter: manifest.RequiresBarrierAfter));
                continue;
            }

            if (StageConflicts(currentManifests, manifest))
                FlushCurrentStage(stages, currentIndices, currentManifests, ref currentRequiresBarrierAfter);

            currentIndices.Add(i);
            currentManifests.Add(manifest);
            currentRequiresBarrierAfter |= manifest.RequiresBarrierAfter;
        }

        FlushCurrentStage(stages, currentIndices, currentManifests, ref currentRequiresBarrierAfter);
        return new SystemSchedule(stages.ToArray());
    }

    private static bool StageConflicts(
        List<SystemAccessManifest> currentManifests,
        SystemAccessManifest manifest)
    {
        for (int i = 0; i < currentManifests.Count; i++)
        {
            if (AccessConflicts.Conflicts(currentManifests[i], manifest))
                return true;
        }

        return false;
    }

    private static void FlushCurrentStage(
        List<SystemScheduleStage> stages,
        List<int> currentIndices,
        List<SystemAccessManifest> currentManifests,
        ref bool currentRequiresBarrierAfter)
    {
        if (currentIndices.Count == 0)
            return;

        stages.Add(new SystemScheduleStage(currentIndices.ToArray(), currentRequiresBarrierAfter));
        currentIndices.Clear();
        currentManifests.Clear();
        currentRequiresBarrierAfter = false;
    }
}

