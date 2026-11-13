namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphDiagnosticsDataValidation
{
    public static IReadOnlyList<string> Validate(RenderGraphDiagnosticsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var errors = new List<string>();
        if (data.Version != RenderGraphDiagnosticsData.CurrentVersion)
            errors.Add($"Unsupported data version {data.Version}.");

        ValidateOrdinals(data.Passes.Select(static row => row.Ordinal), "Pass", errors);
        ValidateOrdinals(data.Buffers.Select(static row => row.Ordinal), "Buffer", errors);
        ValidateOrdinals(data.Textures.Select(static row => row.Ordinal), "Texture", errors);

        ValidateOrdinals(data.QueryPools.Select(static row => row.Ordinal), "QueryPool", errors);
        ValidateOrdinals(data.ShaderTables.Select(static row => row.Ordinal), "ShaderTable", errors);

        var scheduled = new HashSet<int>();
        foreach (RenderGraphDiagnosticsData.Pass pass in data.Passes)
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

        foreach (RenderGraphDiagnosticsData.Access access in data.Accesses)
        {
            if ((uint)access.Pass >= (uint)data.Passes.Length)
                errors.Add($"Access references invalid Pass {access.Pass}.");
            int resourceCount = access.TargetKind switch
            {
                GraphAccessTargetKind.Buffer => data.Buffers.Length,
                GraphAccessTargetKind.Texture => data.Textures.Length,
                GraphAccessTargetKind.QueryPool => data.QueryPools.Length,
                GraphAccessTargetKind.RayTracingShaderTable => data.ShaderTables.Length,
                _ => 0,
            };
            if ((uint)access.TargetOrdinal >= (uint)resourceCount)
                errors.Add($"Access references invalid {access.TargetKind} {access.TargetOrdinal}.");
        }

        foreach (RenderGraphDiagnosticsData.Dependency dependency in data.Dependencies)
        {
            if ((uint)dependency.Predecessor >= (uint)data.Passes.Length ||
                (uint)dependency.Consumer >= (uint)data.Passes.Length)
            {
                errors.Add("Dependency references an invalid Pass.");
            }
        }

        foreach (RenderGraphDiagnosticsData.Barrier barrier in data.Barriers)
            if ((uint)barrier.Pass >= (uint)data.Passes.Length)
                errors.Add($"Barrier references invalid Pass {barrier.Pass}.");

        if (data.Statistics.DeclaredPassCount != data.Passes.Length)
            errors.Add("Declared Pass count does not match Pass rows.");
        if (data.Statistics.AccessCount != data.Accesses.Length)
            errors.Add("Access count does not match Access rows.");
        if (data.Statistics.DependencyCount != data.Dependencies.Length)
            errors.Add("Dependency count does not match Dependency rows.");
        if (data.Statistics.BarrierCount != data.Barriers.Length)
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
