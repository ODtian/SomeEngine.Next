namespace SomeEngine.RenderGraph.Diagnostics;

using System.Collections.Immutable;

public sealed partial class RenderGraphSnapshot
{
    private static ImmutableArray<string> ValidateRows(RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<string> errors = [];
        if (snapshot.Version != RenderGraphSnapshot.CurrentVersion)
            errors.Add($"Unsupported version {snapshot.Version}.");

        ValidateDense(snapshot.Resources, static row => row.Ordinal, "resource", errors);
        ValidateDense(snapshot.Passes, static row => row.Ordinal, "pass", errors);
        ValidateDense(snapshot.Accesses, static row => row.Ordinal, "access", errors);
        ValidateDense(snapshot.Units, static row => row.Ordinal, "unit", errors);
        ValidateDense(snapshot.Tasks, static row => row.Ordinal, "task", errors);
        ValidateDense(snapshot.Batches, static row => row.Ordinal, "batch", errors);

        foreach (RenderGraphSnapshot.Access access in snapshot.Accesses)
        {
            if ((uint)access.PassOrdinal >= (uint)snapshot.Passes.Length)
                errors.Add($"Access {access.Ordinal} refers to pass {access.PassOrdinal}.");
            if ((uint)access.ResourceOrdinal >= (uint)snapshot.Resources.Length)
                errors.Add($"Access {access.Ordinal} refers to resource {access.ResourceOrdinal}.");
        }
        foreach (RenderGraphSnapshot.Pass pass in snapshot.Passes)
        {
            if (pass.ExecutionOrdinal < -1 || pass.ExecutionOrdinal >= snapshot.Passes.Length)
                errors.Add($"Pass {pass.Ordinal} has invalid execution ordinal {pass.ExecutionOrdinal}.");
            if (pass.AccessOffset < 0 ||
                pass.AccessCount < 0 ||
                pass.AccessOffset > snapshot.Accesses.Length - pass.AccessCount)
            {
                errors.Add(
                    $"Pass {pass.Ordinal} has invalid access range [{pass.AccessOffset}, {pass.AccessOffset + pass.AccessCount}).");
            }
            if (pass.DependencyOffset < 0 ||
                pass.DependencyCount < 0 ||
                pass.DependencyOffset > snapshot.Dependencies.Length - pass.DependencyCount)
            {
                errors.Add(
                    $"Pass {pass.Ordinal} has invalid dependency range [{pass.DependencyOffset}, {pass.DependencyOffset + pass.DependencyCount}).");
                continue;
            }
            ReadOnlySpan<int> dependencies = snapshot.Dependencies
                .AsSpan(pass.DependencyOffset, pass.DependencyCount);
            foreach (int dependency in dependencies)
            {
                if ((uint)dependency >= (uint)snapshot.Passes.Length)
                    errors.Add($"Pass {pass.Ordinal} dependency {dependency} is outside the pass rows.");
            }
            if (pass.ShaderArgumentOffset < 0 ||
                pass.ShaderArgumentCount < 0 ||
                pass.ShaderArgumentOffset >
                    snapshot.ShaderArguments.Length - pass.ShaderArgumentCount)
            {
                errors.Add(
                    $"Pass {pass.Ordinal} has invalid shader-argument range " +
                    $"[{pass.ShaderArgumentOffset}, " +
                    $"{pass.ShaderArgumentOffset + pass.ShaderArgumentCount}).");
            }
        }
        foreach (RenderGraphSnapshot.Barrier barrier in snapshot.Barriers)
        {
            if ((uint)barrier.ResourceOrdinal >= (uint)snapshot.Resources.Length)
                errors.Add(
                    $"Barrier at {barrier.Location} {barrier.OwnerOrdinal} refers to resource {barrier.ResourceOrdinal}.");
            if (barrier.AliasingBeforeResourceOrdinal is int beforeResource &&
                (uint)beforeResource >= (uint)snapshot.Resources.Length)
            {
                errors.Add(
                    $"Alias barrier at {barrier.Location} {barrier.OwnerOrdinal} refers to resource {beforeResource}.");
            }
        }
        foreach (RenderGraphSnapshot.Command unit in snapshot.Units)
        {
            ValidateOrdinals(unit.PassOrdinals, snapshot.Passes.Length, $"Unit {unit.Ordinal} pass", errors);
            ValidateOrdinals(unit.Dependencies, snapshot.Units.Length, $"Unit {unit.Ordinal} dependency", errors);
        }
        foreach (RenderGraphSnapshot.Task task in snapshot.Tasks)
            ValidateOrdinals(task.UnitOrdinals, snapshot.Units.Length, $"Task {task.Ordinal} unit", errors);
        foreach (RenderGraphSnapshot.Batch batch in snapshot.Batches)
        {
            ValidateOrdinals(batch.Dependencies, snapshot.Batches.Length, $"Batch {batch.Ordinal} dependency", errors);
            ValidateOrdinals(batch.UnitOrdinals, snapshot.Units.Length, $"Batch {batch.Ordinal} unit", errors);
            ValidateOrdinals(batch.TaskOrdinals, snapshot.Tasks.Length, $"Batch {batch.Ordinal} task", errors);
        }
        long previousFinish = 0;
        foreach (RenderGraphSnapshot.Timing timing in snapshot.Timings)
        {
            if (timing.ClockDomain != ClockDomain.ProcessMonotonic)
                errors.Add($"Timing '{timing.Name}' has unsupported clock domain {timing.ClockDomain}.");
            if (timing.Unit != TimeUnit.Nanosecond)
                errors.Add($"Timing '{timing.Name}' has unsupported unit {timing.Unit}.");
            if (timing.Start < 0 || timing.Close < timing.Start)
                errors.Add($"Timing '{timing.Name}' has an invalid [{timing.Start}, {timing.Close}) interval.");
            if (timing.Start != previousFinish)
                errors.Add($"Timing '{timing.Name}' does not continue the canonical monotonic timeline.");
            previousFinish = timing.Close;
        }
        return [.. errors];
    }

    private static void ValidateDense<T>(
        ImmutableArray<T> rows,
        Func<T, int> ordinal,
        string name,
        List<string> errors)
    {
        for (int index = 0; index < rows.Length; index++)
            if (ordinal(rows[index]) != index) errors.Add($"The {name} row at index {index} has ordinal {ordinal(rows[index])}.");
    }

    private static void ValidateOrdinals(
        ImmutableArray<int> ordinals,
        int count,
        string name,
        List<string> errors)
    {
        foreach (int ordinal in ordinals)
            if ((uint)ordinal >= (uint)count) errors.Add($"{name} {ordinal} is outside [0, {count}).");
    }
}
