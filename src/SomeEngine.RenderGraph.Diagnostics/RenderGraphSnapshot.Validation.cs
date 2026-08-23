namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphSnapshotValidation
{
    public static IReadOnlyList<string> Validate(RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var errors = new List<string>();
        if (snapshot.Version != RenderGraphSnapshot.CurrentVersion)
            errors.Add($"Unsupported snapshot version {snapshot.Version}.");

        ValidateOrdinals(snapshot.Passes.Select(static row => row.Ordinal), "Pass", errors);
        ValidateOrdinals(snapshot.Buffers.Select(static row => row.Ordinal), "Buffer", errors);
        ValidateOrdinals(snapshot.Textures.Select(static row => row.Ordinal), "Texture", errors);

        var scheduled = new HashSet<int>();
        foreach (RenderGraphSnapshot.Pass pass in snapshot.Passes)
        {
            if (pass.Live)
            {
                if (pass.ScheduledOrdinal < 0)
                    errors.Add($"Live Pass {pass.Ordinal} is not scheduled.");
                else if (!scheduled.Add(pass.ScheduledOrdinal))
                    errors.Add($"Scheduled ordinal {pass.ScheduledOrdinal} is duplicated.");
                if (!pass.Queue.HasValue)
                    errors.Add($"Live Pass {pass.Ordinal} has no Queue.");
            }
            else if (pass.ScheduledOrdinal >= 0)
            {
                errors.Add($"Culled Pass {pass.Ordinal} has a scheduled ordinal.");
            }
        }

        foreach (RenderGraphSnapshot.Access access in snapshot.Accesses)
        {
            if ((uint)access.Pass >= (uint)snapshot.Passes.Length)
                errors.Add($"Access references invalid Pass {access.Pass}.");
            int resourceCount = access.TargetKind switch
            {
                GraphAccessTargetKind.Buffer => snapshot.Buffers.Length,
                GraphAccessTargetKind.Texture => snapshot.Textures.Length,
                _ => 0,
            };
            if ((uint)access.TargetOrdinal >= (uint)resourceCount)
                errors.Add($"Access references invalid {access.TargetKind} {access.TargetOrdinal}.");
        }

        foreach (RenderGraphSnapshot.Dependency dependency in snapshot.Dependencies)
        {
            if ((uint)dependency.Predecessor >= (uint)snapshot.Passes.Length ||
                (uint)dependency.Consumer >= (uint)snapshot.Passes.Length)
            {
                errors.Add("Dependency references an invalid Pass.");
            }
        }

        foreach (RenderGraphSnapshot.Barrier barrier in snapshot.Barriers)
            if ((uint)barrier.Pass >= (uint)snapshot.Passes.Length)
                errors.Add($"Barrier references invalid Pass {barrier.Pass}.");

        if (snapshot.Statistics.DeclaredPassCount != snapshot.Passes.Length)
            errors.Add("Declared Pass count does not match Pass rows.");
        if (snapshot.Statistics.AccessCount != snapshot.Accesses.Length)
            errors.Add("Access count does not match Access rows.");
        if (snapshot.Statistics.DependencyCount != snapshot.Dependencies.Length)
            errors.Add("Dependency count does not match Dependency rows.");
        if (snapshot.Statistics.BarrierCount != snapshot.Barriers.Length)
            errors.Add("Barrier count does not match Barrier rows.");
        return errors;
    }

    private static void ValidateOrdinals(
        IEnumerable<int> ordinals,
        string rowName,
        List<string> errors)
    {
        int expected = 0;
        foreach (int ordinal in ordinals)
        {
            if (ordinal != expected)
                errors.Add($"{rowName} ordinal {ordinal} is not contiguous at {expected}.");
            expected++;
        }
    }
}
