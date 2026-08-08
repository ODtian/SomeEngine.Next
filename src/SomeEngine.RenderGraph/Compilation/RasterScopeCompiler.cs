namespace SomeEngine.RenderGraph;

internal enum PassBreakReason : byte
{
    NonRaster,
    Queue,
    PassPolicy,
    ExtentOrSamples,
    AttachmentSet,
    LoadType,
    DepthStencilMode,
    Barrier,
    AliasBarrier,
    CrossQueueSynchronization,
    ExternalReadiness,
    Resolve,
}

internal readonly record struct RasterStatistics(
    bool Enabled,
    int LiveRasterPasses,
    int CandidateScopes,
    int MergedLogicalPasses,
    int CommandUnitCount,
    ArenaSlice<int> BreakReasonCounts);

internal static class RasterScopeCompiler
{
    public static void Group(
        RenderGraph graph,
        ReadOnlySpan<int> activePassOrdinals,
        ArenaSlice<QueueType> queues,
        ArenaSlice<Extent2D> rendering,
        ArenaSlice<PlannedAliasingBarrier> aliasAcquires,
        ReachabilityTable reachability,
        out ArenaColumn<int> passRows,
        out ArenaColumn<int> groupStarts,
        out RasterStatistics statistics)
    {
        passRows = graph.CreateArenaColumn<int>();
        groupStarts = graph.CreateArenaColumn<int>();
        passRows.EnsureCapacity(activePassOrdinals.Length);
        groupStarts.EnsureCapacity(checked(activePassOrdinals.Length + 1));
        ArenaSlice<byte> aliasBoundaries = graph.AllocateSlice<byte>(graph.Passes.Length);
        foreach (PlannedAliasingBarrier acquire in aliasAcquires)
        foreach (int pass in acquire.StartPasses)
            aliasBoundaries[pass] = 1;
        ArenaSlice<int> breakReasons = graph.AllocateSlice<int>(
            Enum.GetValues<PassBreakReason>().Length);
        ArenaSlice<byte> seenImportedResources = graph.AllocateSlice<byte>(
            checked(graph.ResourceCount * 3));
        int candidateScopes = 0;
        bool previousWasCandidate = false;
        foreach (int pass in activePassOrdinals)
        {
            if (groupStarts.Count == 0)
            {
                int offset = passRows.Count;
                groupStarts.Add(offset);
                passRows.Add(pass);
                AddImportedResources(graph, pass, queues[pass], seenImportedResources);
                continue;
            }

            int currentOffset = groupStarts[groupStarts.Count - 1];
            int currentCount = passRows.Count - currentOffset;
            ReadOnlySpan<int> currentPasses =
                passRows.GetReadOnlySpan(currentOffset, currentCount);
            PassBreakReason? reason = BreakReason(
                graph,
                currentPasses,
                pass,
                queues,
                rendering,
                aliasBoundaries,
                reachability,
                seenImportedResources);
            if (reason is null)
            {
                if (!previousWasCandidate) candidateScopes++;
                previousWasCandidate = true;
                passRows.Add(pass);
                AddImportedResources(graph, pass, queues[pass], seenImportedResources);
                continue;
            }
            else
            {
                breakReasons[(int)reason.Value]++;
                previousWasCandidate = false;
            }
            int nextOffset = passRows.Count;
            groupStarts.Add(nextOffset);
            passRows.Add(pass);
            AddImportedResources(graph, pass, queues[pass], seenImportedResources);
        }

        int liveRasterPasses = 0;
        foreach (int pass in activePassOrdinals)
            if (GetExtent2D(rendering, pass).IsValid) liveRasterPasses++;
        int mergedLogicalPasses = 0;
        int groupCount = groupStarts.Count;
        for (int group = 0; group < groupCount; group++)
        {
            int afterLast = group + 1 < groupCount
                ? groupStarts[group + 1]
                : passRows.Count;
            int count = afterLast - groupStarts[group];
            if (count > 1) mergedLogicalPasses += count;
        }
        groupStarts.Add(passRows.Count);
        statistics = new RasterStatistics(
            Enabled: true,
            liveRasterPasses,
            candidateScopes,
            mergedLogicalPasses,
            groupCount,
            breakReasons);
    }

    private static PassBreakReason? BreakReason(
        RenderGraph graph,
        ReadOnlySpan<int> currentScope,
        int next,
        ArenaSlice<QueueType> queues,
        ArenaSlice<Extent2D> rendering,
        ArenaSlice<byte> aliasBoundaries,
        ReachabilityTable reachability,
        ArenaSlice<byte> seenImportedResources)
    {
        int previous = currentScope[^1];
        Extent2D left = GetExtent2D(rendering, previous);
        Extent2D right = GetExtent2D(rendering, next);
        if (!left.IsValid || !right.IsValid)
            return PassBreakReason.NonRaster;
        if (queues[previous] != QueueType.Graphics || queues[next] != QueueType.Graphics)
            return PassBreakReason.Queue;
        if ((graph.Passes[previous].Flags & PassFlags.NeverMerge) != 0 ||
            (graph.Passes[next].Flags & PassFlags.NeverMerge) != 0)
            return PassBreakReason.PassPolicy;
        if ((graph.Passes[previous].Flags & PassFlags.NeverParallel) != 0 ||
            (graph.Passes[next].Flags & PassFlags.NeverParallel) != 0)
            return PassBreakReason.PassPolicy;
        if (left != right) return PassBreakReason.ExtentOrSamples;
        if (aliasBoundaries[next] != 0) return PassBreakReason.AliasBarrier;
        if (graph.GetAfterBarriers(previous).Length != 0 || graph.GetBeforeBarriers(next).Length != 0)
            return PassBreakReason.Barrier;
        if (ChangesCrossQueueSchedule(currentScope, next, queues, reachability))
            return PassBreakReason.CrossQueueSynchronization;
        ref readonly PassData first = ref graph.Passes[previous];
        ref readonly PassData second = ref graph.Passes[next];
        ReadOnlySpan<PassFragmentData> firstColors = graph.GetPassColorAttachments(first);
        ReadOnlySpan<PassFragmentData> secondColors = graph.GetPassColorAttachments(second);
        foreach (PassFragmentData attachment in firstColors)
            if (attachment.HasResolve) return PassBreakReason.Resolve;
        if (firstColors.Length != secondColors.Length)
            return PassBreakReason.AttachmentSet;
        for (int index = 0; index < firstColors.Length; index++)
        {
            PassFragmentData firstColor = firstColors[index];
            PassFragmentData secondColor = secondColors[index];
            if (firstColor.Slot != secondColor.Slot || firstColor.View != secondColor.View)
                return PassBreakReason.AttachmentSet;
            if (secondColor.Load != LoadType.Load) return PassBreakReason.LoadType;
        }

        PassFragmentData? firstDepthStencil = graph.GetPassDepthStencilAttachment(first);
        PassFragmentData? secondDepthStencil = graph.GetPassDepthStencilAttachment(second);
        if (firstDepthStencil is null != (secondDepthStencil is null))
            return PassBreakReason.AttachmentSet;
        if (firstDepthStencil is PassFragmentData firstDepth &&
            secondDepthStencil is PassFragmentData secondDepth)
        {
            if (firstDepth.View != secondDepth.View ||
                firstDepth.HasDepth != secondDepth.HasDepth ||
                firstDepth.HasStencil != secondDepth.HasStencil)
            {
                return PassBreakReason.AttachmentSet;
            }
            if (firstDepth.HasDepth)
            {
                if (firstDepth.DepthReadOnly != secondDepth.DepthReadOnly)
                    return PassBreakReason.DepthStencilMode;
                if (secondDepth.DepthLoad != LoadType.Load) return PassBreakReason.LoadType;
            }
            if (firstDepth.HasStencil)
            {
                if (firstDepth.StencilReadOnly != secondDepth.StencilReadOnly)
                    return PassBreakReason.DepthStencilMode;
                if (secondDepth.StencilLoad != LoadType.Load) return PassBreakReason.LoadType;
            }
        }
        foreach (ref readonly PassInputData access in graph.GetPassAccesses(graph.Passes[next]))
        {
            int resource = graph.GetResourceOrdinal(access);
            if (seenImportedResources[checked(resource * 3 + (int)queues[next])] == 0 &&
                HasCrossQueueReadiness(graph, resource, queues[next]))
                return PassBreakReason.ExternalReadiness;
        }
        return null;
    }

    private static Extent2D GetExtent2D(
        ArenaSlice<Extent2D> rendering,
        int pass) => rendering.IsEmpty ? default : rendering[pass];

    private static bool HasCrossQueueReadiness(
        RenderGraph graph,
        int resource,
        QueueType queue)
    {
        if (!graph.IsResourceImported(resource)) return false;
        foreach (QueueCompletion completion in graph.GetResourceReadiness(resource))
            if (completion.Queue.Type != queue) return true;
        return false;
    }

    private static bool ChangesCrossQueueSchedule(
        ReadOnlySpan<int> currentScope,
        int next,
        ArenaSlice<QueueType> queues,
        ReachabilityTable reachability)
    {
        foreach (int other in reachability.ActivePassOrdinals)
        {
            if (queues[other] == QueueType.Graphics || other == next || currentScope.Contains(other)) continue;
            bool entersNext = reachability.Before(other, next);
            bool entersWholeScope = true;
            bool leavesScope = false;
            foreach (int pass in currentScope)
            {
                if (!reachability.Before(other, pass)) entersWholeScope = false;
                if (reachability.Before(pass, other)) leavesScope = true;
            }
            if (entersNext && !entersWholeScope) return true;
            if (leavesScope && !reachability.Before(next, other)) return true;
        }
        return false;
    }

    private static void AddImportedResources(
        RenderGraph graph,
        int pass,
        QueueType queue,
        ArenaSlice<byte> seen)
    {
        foreach (ref readonly PassInputData access in graph.GetPassAccesses(graph.Passes[pass]))
        {
            int resource = graph.GetResourceOrdinal(access);
            if (graph.IsResourceImported(resource))
                seen[checked(resource * 3 + (int)queue)] = 1;
        }
    }
}
