namespace SomeEngine.RenderGraph;

internal enum RasterMergeBreakReason : byte
{
    NonRaster,
    Queue,
    RecordingLane,
    ExtentOrSamples,
    AttachmentSet,
    LoadAction,
    DepthStencilMode,
    Barrier,
    AliasAcquire,
    CrossQueueSynchronization,
    ExternalReadiness,
}

internal sealed class RasterGrouping
{
    public RasterGrouping(int[][] logicalPassGroups, CompiledRasterStatistics statistics)
    {
        LogicalPassGroups = logicalPassGroups;
        Statistics = statistics;
    }

    public int[][] LogicalPassGroups { get; }
    public CompiledRasterStatistics Statistics { get; }
}

internal readonly record struct CompiledRasterStatistics(
    bool Enabled,
    int LiveRasterPasses,
    int CandidateScopes,
    int MergedLogicalPasses,
    int RecordUnitCount,
    int[] BreakReasonCounts);

internal static class RasterScopeCompiler
{
    public static RasterGrouping WithoutMerging(
        int[] activePassOrdinals,
        CompiledRendering?[] rendering)
    {
        int[][] groups = activePassOrdinals.Select(static pass => new[] { pass }).ToArray();
        return new RasterGrouping(
            groups,
            new CompiledRasterStatistics(
                Enabled: false,
                LiveRasterPasses: activePassOrdinals.Count(pass => rendering[pass] is not null),
                CandidateScopes: 0,
                MergedLogicalPasses: 0,
                RecordUnitCount: groups.Length,
                BreakReasonCounts: new int[Enum.GetValues<RasterMergeBreakReason>().Length]));
    }

    public static RasterGrouping Group(
        FrozenGraph graph,
        int[] activePassOrdinals,
        QueueType[] queues,
        CompiledRendering?[] rendering,
        BarrierTemplate[][] beforeBarriers,
        BarrierTemplate[][] afterBarriers,
        AliasAcquireEdge[] aliasAcquires,
        PassReachability reachability,
        bool enableMerging)
    {
        HashSet<int> aliasBoundaries = aliasAcquires
            .SelectMany(static acquire => acquire.StartPasses)
            .ToHashSet();
        int[] breakReasons = new int[Enum.GetValues<RasterMergeBreakReason>().Length];
        List<List<int>> groups = [];
        HashSet<(int Resource, QueueType Queue)> seenImportedResources = [];
        int candidateScopes = 0;
        bool previousWasCandidate = false;
        foreach (int pass in activePassOrdinals)
        {
            if (groups.Count == 0)
            {
                groups.Add([pass]);
                AddImportedResources(graph, pass, queues[pass], seenImportedResources);
                continue;
            }

            List<int> current = groups[^1];
            RasterMergeBreakReason? reason = BreakReason(
                graph,
                current,
                pass,
                queues,
                rendering,
                beforeBarriers,
                afterBarriers,
                aliasBoundaries,
                reachability,
                seenImportedResources);
            if (reason is null)
            {
                if (!previousWasCandidate) candidateScopes++;
                previousWasCandidate = true;
                if (enableMerging)
                {
                    current.Add(pass);
                    AddImportedResources(graph, pass, queues[pass], seenImportedResources);
                    continue;
                }
            }
            else
            {
                breakReasons[(int)reason.Value]++;
                previousWasCandidate = false;
            }
            groups.Add([pass]);
            AddImportedResources(graph, pass, queues[pass], seenImportedResources);
        }

        int[][] result = groups.Select(static group => group.ToArray()).ToArray();
        int liveRasterPasses = activePassOrdinals.Count(pass => rendering[pass] is not null);
        int mergedLogicalPasses = enableMerging
            ? result.Where(static group => group.Length > 1).Sum(static group => group.Length)
            : 0;
        return new RasterGrouping(
            result,
            new CompiledRasterStatistics(
                enableMerging,
                liveRasterPasses,
                candidateScopes,
                mergedLogicalPasses,
                result.Length,
                breakReasons));
    }

    private static RasterMergeBreakReason? BreakReason(
        FrozenGraph graph,
        List<int> currentScope,
        int next,
        QueueType[] queues,
        CompiledRendering?[] rendering,
        BarrierTemplate[][] beforeBarriers,
        BarrierTemplate[][] afterBarriers,
        HashSet<int> aliasBoundaries,
        PassReachability reachability,
        HashSet<(int Resource, QueueType Queue)> seenImportedResources)
    {
        int previous = currentScope[^1];
        if (rendering[previous] is not CompiledRendering left || rendering[next] is not CompiledRendering right)
            return RasterMergeBreakReason.NonRaster;
        if (queues[previous] != QueueType.Graphics || queues[next] != QueueType.Graphics)
            return RasterMergeBreakReason.Queue;
        if (graph.Passes[previous].RecordingLane != graph.Passes[next].RecordingLane)
            return RasterMergeBreakReason.RecordingLane;
        if (left != right) return RasterMergeBreakReason.ExtentOrSamples;
        if (aliasBoundaries.Contains(next)) return RasterMergeBreakReason.AliasAcquire;
        if (afterBarriers[previous].Length != 0 || beforeBarriers[next].Length != 0)
            return RasterMergeBreakReason.Barrier;
        if (ChangesCrossQueueSchedule(currentScope, next, queues, reachability))
            return RasterMergeBreakReason.CrossQueueSynchronization;
        FrozenPass first = graph.Passes[previous];
        FrozenPass second = graph.Passes[next];
        if (first.ColorAttachments.Length != second.ColorAttachments.Length)
            return RasterMergeBreakReason.AttachmentSet;
        for (int index = 0; index < first.ColorAttachments.Length; index++)
        {
            FrozenColorAttachment firstColor = first.ColorAttachments[index];
            FrozenColorAttachment secondColor = second.ColorAttachments[index];
            if (firstColor.Slot != secondColor.Slot || firstColor.View != secondColor.View)
                return RasterMergeBreakReason.AttachmentSet;
            if (secondColor.Load != LoadAction.Load) return RasterMergeBreakReason.LoadAction;
        }

        if (first.DepthStencilAttachment is null != (second.DepthStencilAttachment is null))
            return RasterMergeBreakReason.AttachmentSet;
        if (first.DepthStencilAttachment is FrozenDepthStencilAttachment firstDepth &&
            second.DepthStencilAttachment is FrozenDepthStencilAttachment secondDepth)
        {
            if (firstDepth.View != secondDepth.View ||
                (firstDepth.Depth is null) != (secondDepth.Depth is null) ||
                (firstDepth.Stencil is null) != (secondDepth.Stencil is null))
            {
                return RasterMergeBreakReason.AttachmentSet;
            }
            if (firstDepth.Depth is DepthAttachmentOps firstDepthOps &&
                secondDepth.Depth is DepthAttachmentOps secondDepthOps)
            {
                if (firstDepthOps.ReadOnly != secondDepthOps.ReadOnly)
                    return RasterMergeBreakReason.DepthStencilMode;
                if (secondDepthOps.Load != LoadAction.Load) return RasterMergeBreakReason.LoadAction;
            }
            if (firstDepth.Stencil is StencilAttachmentOps firstStencilOps &&
                secondDepth.Stencil is StencilAttachmentOps secondStencilOps)
            {
                if (firstStencilOps.ReadOnly != secondStencilOps.ReadOnly)
                    return RasterMergeBreakReason.DepthStencilMode;
                if (secondStencilOps.Load != LoadAction.Load) return RasterMergeBreakReason.LoadAction;
            }
        }
        if (graph.Passes[next].Accesses.Any(access =>
                !seenImportedResources.Contains((access.Resource, queues[next])) &&
                HasCrossQueueReadiness(graph.Resources[access.Resource], queues[next])))
        {
            return RasterMergeBreakReason.ExternalReadiness;
        }
        return null;
    }

    private static bool HasCrossQueueReadiness(in FrozenResource resource, QueueType queue)
    {
        if (!resource.IsImported) return false;
        GpuCompletion[] readiness = resource.Kind == ResourceNodeKind.Buffer
            ? resource.ImportedBuffer.Readiness ?? []
            : resource.ImportedTexture.Readiness ?? [];
        return readiness.Any(completion => completion.Queue != queue);
    }

    private static bool ChangesCrossQueueSchedule(
        List<int> currentScope,
        int next,
        QueueType[] queues,
        PassReachability reachability)
    {
        foreach (int other in reachability.ActivePassOrdinals)
        {
            if (queues[other] == QueueType.Graphics || other == next || currentScope.Contains(other)) continue;
            bool entersNext = reachability.Before(other, next);
            if (entersNext && currentScope.Any(pass => !reachability.Before(other, pass))) return true;
            bool leavesScope = currentScope.Any(pass => reachability.Before(pass, other));
            if (leavesScope && !reachability.Before(next, other)) return true;
        }
        return false;
    }

    private static void AddImportedResources(
        FrozenGraph graph,
        int pass,
        QueueType queue,
        HashSet<(int Resource, QueueType Queue)> seen)
    {
        foreach (FrozenAccess access in graph.Passes[pass].Accesses)
            if (graph.Resources[access.Resource].IsImported) seen.Add((access.Resource, queue));
    }
}
