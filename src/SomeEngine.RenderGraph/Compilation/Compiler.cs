namespace SomeEngine.RenderGraph;

internal static class Compiler
{
    public static CompiledGraph Compile(FrozenGraph graph, DeviceCompilationSnapshot device, bool optimized)
        => Compile(
            graph,
            device,
            optimized,
            enableTransientAliasing: false,
            enableRenderPassMerging: false);

    internal static CompiledGraph Compile(
        FrozenGraph graph,
        DeviceCompilationSnapshot device,
        bool optimized,
        bool enableTransientAliasing,
        bool enableRenderPassMerging = false)
    {
        ValidateShaderContracts(graph);
        ValidatePassLocalAccesses(graph);
        ValidateContents(graph);
        GraphLiveness liveness = GraphLiveness.Analyze(graph);

        int passCount = graph.Passes.Length;
        QueueType[] queues = new QueueType[passCount];
        for (int pass = 0; pass < passCount; pass++)
        {
            _ = graph.Passes[pass].Queues.ToArray();
            queues[pass] = liveness.Passes[pass]
                ? graph.Passes[pass].Queues.Select(device)
                : graph.Passes[pass].Queues.First;
        }

        ValidateResourceContracts(graph, queues, liveness);
        CompiledRendering?[] rendering = BuildRenderingPlans(graph, queues, liveness);
        int[][] dependencies = BuildDependencies(graph, queues, liveness);
        bool useAliasing = optimized && enableTransientAliasing;
        bool useRasterMerging = optimized && enableRenderPassMerging;
        PassReachability? reachability = useAliasing || useRasterMerging
            ? new PassReachability(liveness.ActivePassOrdinals, dependencies, queues)
            : null;
        BuildBarriers(
            graph,
            liveness,
            queues,
            out BarrierTemplate[][] before,
            out BarrierTemplate[][] after,
            out InternalBarrierEdge[] internalBarriers);
        TransientPlacement placement = TransientAliasAllocator.Place(
            graph,
            liveness.Resources,
            reachability,
            useAliasing);
        RasterGrouping raster = useRasterMerging
            ? RasterScopeCompiler.Group(
                graph,
                liveness.ActivePassOrdinals,
                queues,
                rendering,
                before,
                after,
                placement.Acquires,
                reachability!,
                enableMerging: true)
            : RasterScopeCompiler.WithoutMerging(
                liveness.ActivePassOrdinals,
                rendering);
        CompiledCullingStatistics culling = BuildCullingStatistics(graph, liveness);
        BuildExecution(
            graph,
            queues,
            raster.LogicalPassGroups,
            dependencies,
            placement.Acquires,
            internalBarriers,
            out CompiledExecutionBatch[] executionBatches,
            out CompiledRecordUnit[] recordUnits,
            out int[] passToRecordUnit);
        return new CompiledGraph(
            queues,
            liveness.ActivePassOrdinals,
            liveness.Roots,
            liveness.RetainingPasses,
            liveness.Resources,
            liveness.BufferViews,
            liveness.TextureViews,
            executionBatches,
            recordUnits,
            passToRecordUnit,
            placement.Statistics,
            raster.Statistics,
            culling,
            dependencies,
            before,
            after,
            placement.Heaps,
            placement.Placements,
            rendering,
            optimized);
    }

    private static CompiledCullingStatistics BuildCullingStatistics(FrozenGraph graph, GraphLiveness liveness)
    {
        int declaredViews = checked(graph.BufferViews.Length + graph.TextureViews.Length);
        int liveViews = checked(liveness.BufferViews.Count(static live => live) + liveness.TextureViews.Count(static live => live));
        ulong culledTransientBytes = 0;
        for (int resource = 0; resource < graph.Resources.Length; resource++)
        {
            if (!liveness.Resources[resource] && !graph.Resources[resource].IsImported)
                culledTransientBytes = checked(culledTransientBytes + graph.Resources[resource].Requirements.Size);
        }
        int liveResources = liveness.Resources.Count(static live => live);
        return new CompiledCullingStatistics(
            graph.Passes.Length,
            liveness.ActivePassOrdinals.Length,
            graph.Passes.Length - liveness.ActivePassOrdinals.Length,
            graph.Resources.Length,
            liveResources,
            graph.Resources.Length - liveResources,
            declaredViews,
            liveViews,
            declaredViews - liveViews,
            culledTransientBytes,
            liveness.Roots.Count(static root => root));
    }

    private static void BuildExecution(
        FrozenGraph graph,
        QueueType[] queues,
        int[][] logicalPassGroups,
        int[][] passDependencies,
        AliasAcquireEdge[] aliasAcquires,
        InternalBarrierEdge[] internalBarriers,
        out CompiledExecutionBatch[] batches,
        out CompiledRecordUnit[] recordUnits,
        out int[] passToRecordUnit)
    {
        Dictionary<int, ExecutionNode> passNodes = [];
        List<ExecutionNode> nodes = [];
        foreach (int[] group in logicalPassGroups)
        {
            if (group.Length == 0) throw new InvalidOperationException("A logical record group cannot be empty.");
            ExecutionNode node = ExecutionNode.Logical(group, queues[group[0]]);
            nodes.Add(node);
            foreach (int pass in group)
            {
                if (queues[pass] != node.Queue || !passNodes.TryAdd(pass, node))
                    throw new InvalidOperationException("Logical record groups must uniquely contain same-queue passes.");
            }
        }
        foreach ((int pass, ExecutionNode node) in passNodes)
        foreach (int predecessor in passDependencies[pass])
            if (!ReferenceEquals(node, passNodes[predecessor])) node.Predecessors.Add(passNodes[predecessor]);

        int[] previousOnQueue = Enumerable.Repeat(-1, Enum.GetValues<QueueType>().Length).ToArray();
        foreach (int[] group in logicalPassGroups)
        {
            ExecutionNode node = passNodes[group[0]];
            int previous = previousOnQueue[(int)node.Queue];
            if (previous >= 0 && !ReferenceEquals(node, passNodes[previous])) node.Predecessors.Add(passNodes[previous]);
            previousOnQueue[(int)node.Queue] = group[^1];
        }

        foreach (AliasAcquireEdge acquire in aliasAcquires.OrderBy(static value => value.StartPasses[0]).ThenBy(static value => value.AfterResource))
        {
            ExecutionNode node = ExecutionNode.Alias(acquire);
            foreach (int predecessor in acquire.EndPasses) node.Predecessors.Add(passNodes[predecessor]);
            foreach (int successor in acquire.StartPasses) passNodes[successor].Predecessors.Add(node);
            nodes.Add(node);
        }

        foreach (InternalBarrierEdge edge in internalBarriers
                     .OrderBy(static value => value.SortPass)
                     .ThenBy(static value => value.StableOrdinal))
        {
            ExecutionNode node = ExecutionNode.CreateInternalBarriers(edge);
            foreach (int predecessor in edge.PredecessorPasses)
                node.Predecessors.Add(passNodes[predecessor]);
            foreach (int successor in edge.SuccessorPasses)
                passNodes[successor].Predecessors.Add(node);
            nodes.Add(node);
        }

        List<ExecutionNode> ordered = [];
        HashSet<ExecutionNode> emitted = [];
        while (ordered.Count != nodes.Count)
        {
            ExecutionNode? next = nodes
                .Where(node => !emitted.Contains(node) && node.Predecessors.All(emitted.Contains))
                .OrderBy(static node => node.SortPass)
                .ThenBy(static node => node.Kind == CompiledRecordUnitKind.AliasAcquire ? 0 : 1)
                .ThenBy(static node => node.StableOrdinal)
                .FirstOrDefault();
            if (next is null) throw new InvalidOperationException("Compiled execution topology contains a cycle.");
            emitted.Add(next);
            ordered.Add(next);
        }

        recordUnits = new CompiledRecordUnit[ordered.Count];
        passToRecordUnit = Enumerable.Repeat(-1, graph.Passes.Length).ToArray();
        Dictionary<ExecutionNode, int> nodeOrdinals = [];
        for (int index = 0; index < ordered.Count; index++) nodeOrdinals.Add(ordered[index], index);
        for (int index = 0; index < ordered.Count; index++)
        {
            ExecutionNode node = ordered[index];
            int[] logicalPasses = node.LogicalPasses;
            CompiledAliasAcquire[] acquires = node.AliasAcquire is AliasAcquireEdge acquire
                ? [new CompiledAliasAcquire(acquire.BeforeResource, acquire.AfterResource)]
                : [];
            recordUnits[index] = new CompiledRecordUnit(
                node.Queue,
                node.Kind,
                logicalPasses,
                acquires,
                node.InternalBarriers);
            foreach (int pass in node.LogicalPasses) passToRecordUnit[pass] = index;
        }

        Dictionary<ExecutionNode, HashSet<ExecutionNode>> successors = ordered
            .ToDictionary(static node => node, static _ => new HashSet<ExecutionNode>());
        foreach (ExecutionNode node in ordered)
        foreach (ExecutionNode predecessor in node.Predecessors)
            successors[predecessor].Add(node);

        List<List<ExecutionNode>> batchGroups = [];
        HashSet<(int Resource, QueueType Queue)> seenImportedResources = [];
        foreach (ExecutionNode node in ordered)
        {
            if (batchGroups.Count == 0)
            {
                batchGroups.Add([node]);
                AddImportedResources(graph, node, seenImportedResources);
                continue;
            }

            List<ExecutionNode> current = batchGroups[^1];
            ExecutionNode previous = current[^1];
            HashSet<ExecutionNode> establishedCrossQueueInputs = current
                .SelectMany(static value => value.Predecessors)
                .Where(predecessor => predecessor.Queue != node.Queue)
                .ToHashSet();
            bool introducesCrossQueueInput = node.Predecessors.Any(
                predecessor => predecessor.Queue != node.Queue &&
                               !establishedCrossQueueInputs.Contains(predecessor));
            bool exposesCrossQueueOutput = successors[previous].Any(
                successor => successor.Queue != previous.Queue);
            bool introducesExternalReadiness = IntroducesCrossQueueReadiness(
                graph,
                node,
                seenImportedResources);
            if (node.Queue == previous.Queue &&
                !introducesCrossQueueInput &&
                !exposesCrossQueueOutput &&
                !introducesExternalReadiness)
            {
                current.Add(node);
            }
            else
            {
                batchGroups.Add([node]);
            }
            AddImportedResources(graph, node, seenImportedResources);
        }

        Dictionary<ExecutionNode, int> nodeToBatch = [];
        for (int batch = 0; batch < batchGroups.Count; batch++)
        foreach (ExecutionNode node in batchGroups[batch])
            nodeToBatch.Add(node, batch);

        batches = new CompiledExecutionBatch[batchGroups.Count];
        for (int batch = 0; batch < batchGroups.Count; batch++)
        {
            List<ExecutionNode> group = batchGroups[batch];
            int[] dependencies = group
                .SelectMany(static node => node.Predecessors)
                .Select(predecessor => nodeToBatch[predecessor])
                .Where(predecessor => predecessor != batch)
                .Distinct()
                .Order()
                .ToArray();
            if (dependencies.Any(predecessor => predecessor >= batch))
                throw new InvalidOperationException("Execution dependencies must precede their dependent batch.");
            batches[batch] = new CompiledExecutionBatch(
                group[0].Queue,
                dependencies,
                group.Select(node => nodeOrdinals[node]).ToArray());
        }
    }

    private static bool IntroducesCrossQueueReadiness(
        FrozenGraph graph,
        ExecutionNode node,
        HashSet<(int Resource, QueueType Queue)> seen)
    {
        foreach (int resource in node.LogicalPasses
                     .SelectMany(pass => graph.Passes[pass].Accesses)
                     .Select(static access => access.Resource)
                     .Distinct())
        {
            FrozenResource value = graph.Resources[resource];
            if (!value.IsImported || seen.Contains((resource, node.Queue))) continue;
            GpuCompletion[] readiness = value.Kind == ResourceNodeKind.Buffer
                ? value.ImportedBuffer.Readiness ?? []
                : value.ImportedTexture.Readiness ?? [];
            if (readiness.Any(completion => completion.Queue != node.Queue)) return true;
        }
        return false;
    }

    private static void AddImportedResources(
        FrozenGraph graph,
        ExecutionNode node,
        HashSet<(int Resource, QueueType Queue)> seen)
    {
        foreach (int resource in node.LogicalPasses
                     .SelectMany(pass => graph.Passes[pass].Accesses)
                     .Select(static access => access.Resource)
                     .Distinct())
        {
            if (graph.Resources[resource].IsImported) seen.Add((resource, node.Queue));
        }
    }

    private sealed class ExecutionNode
    {
        private ExecutionNode(
            QueueType queue,
            CompiledRecordUnitKind kind,
            int[] logicalPasses,
            AliasAcquireEdge? aliasAcquire,
            BarrierTemplate[] internalBarriers,
            int sortPass,
            int stableOrdinal)
        {
            Queue = queue;
            Kind = kind;
            LogicalPasses = logicalPasses;
            AliasAcquire = aliasAcquire;
            InternalBarriers = internalBarriers;
            SortPass = sortPass;
            StableOrdinal = stableOrdinal;
        }

        public QueueType Queue { get; }
        public CompiledRecordUnitKind Kind { get; }
        public int[] LogicalPasses { get; }
        public AliasAcquireEdge? AliasAcquire { get; }
        public BarrierTemplate[] InternalBarriers { get; }
        public int SortPass { get; }
        public int StableOrdinal { get; }
        public HashSet<ExecutionNode> Predecessors { get; } = [];

        public static ExecutionNode Logical(int[] passes, QueueType queue) =>
            new(
                queue,
                passes.Length > 1 ? CompiledRecordUnitKind.RasterScope : CompiledRecordUnitKind.Standalone,
                passes,
                null,
                [],
                passes[0],
                passes[0]);

        public static ExecutionNode Alias(in AliasAcquireEdge acquire) =>
            new(
                QueueType.Graphics,
                CompiledRecordUnitKind.AliasAcquire,
                [],
                acquire,
                [],
                acquire.StartPasses[0],
                acquire.AfterResource);

        public static ExecutionNode CreateInternalBarriers(in InternalBarrierEdge edge) =>
            new(
                QueueType.Graphics,
                CompiledRecordUnitKind.InternalBarriers,
                [],
                null,
                edge.Barriers,
                edge.SortPass,
                edge.StableOrdinal);
    }

    private static void ValidateShaderContracts(FrozenGraph graph)
    {
        foreach (FrozenPass pass in graph.Passes)
        {
            foreach (FrozenShaderContract shader in pass.Shaders)
            {
                ShaderContractValidator.Validate(
                    shader,
                    pass.Name,
                    pass.Accesses,
                    graph.BufferViews,
                    graph.TextureViews);
            }
        }
    }

    private static void ValidatePassLocalAccesses(FrozenGraph graph)
    {
        foreach (FrozenPass pass in graph.Passes)
        {
            for (int current = 0; current < pass.Accesses.Length; current++)
            {
                for (int previous = 0; previous < current; previous++)
                {
                    if (AccessNormalizer.Overlaps(pass.Accesses[current], pass.Accesses[previous]))
                    {
                        throw new InvalidOperationException($"Pass '{pass.Name}' declares overlapping accesses to one resource; declare one joined ReadWrite access instead.");
                    }
                }
            }
        }
    }

    private static void ValidateContents(FrozenGraph graph)
    {
        IntervalSet?[] bufferContents = new IntervalSet?[graph.Resources.Length];
        HashSet<TextureCell>?[] textureContents = new HashSet<TextureCell>?[graph.Resources.Length];
        for (int resource = 0; resource < graph.Resources.Length; resource++)
        {
            FrozenResource value = graph.Resources[resource];
            if (value.Kind == ResourceNodeKind.Buffer)
            {
                IntervalSet set = new();
                if (value.IsImported && value.ImportedBuffer.ContentsAvailable) set.Add(0, value.BufferDesc.Size);
                bufferContents[resource] = set;
            }
            else
            {
                HashSet<TextureCell> set = new();
                if (value.IsImported && value.ImportedTexture.ContentsAvailable)
                {
                    TextureSubresourceRange whole = new(
                        0,
                        value.TextureDesc.MipLevels,
                        0,
                        value.TextureDesc.ArrayLayers,
                        AspectFor(value.TextureDesc.Format));
                    foreach (TextureCell cell in EnumerateCells(value.TextureDesc, whole)) set.Add(cell);
                }
                textureContents[resource] = set;
            }
        }

        foreach (FrozenPass pass in graph.Passes)
        {
            foreach (FrozenAccess access in pass.Accesses)
            {
                bool requiresContents = access.Effect != ResourceEffect.Write || access.PriorContents == PriorContents.Required;
                if (access.Kind == ResourceNodeKind.Buffer)
                {
                    IntervalSet set = bufferContents[access.Resource]!;
                    ulong end = checked(access.BufferRange.Offset + access.BufferRange.Size);
                    if (requiresContents && !set.Contains(access.BufferRange.Offset, end))
                        throw new InvalidOperationException($"Pass '{pass.Name}' reads buffer content that has not been imported or fully produced.");
                    if (access.Effect != ResourceEffect.Read && access.PriorContents == PriorContents.Discard)
                        set.Remove(access.BufferRange.Offset, end);
                    if (access.Effect != ResourceEffect.Read && access.Coverage == WriteCoverage.Full) set.Add(access.BufferRange.Offset, end);
                }
                else
                {
                    FrozenResource resource = graph.Resources[access.Resource];
                    HashSet<TextureCell> set = textureContents[access.Resource]!;
                    foreach (TextureCell cell in EnumerateCells(resource.TextureDesc, access.TextureRange))
                    {
                        if (requiresContents && !set.Contains(cell))
                            throw new InvalidOperationException($"Pass '{pass.Name}' reads texture content that has not been imported or fully produced.");
                        if (access.Effect != ResourceEffect.Read && access.PriorContents == PriorContents.Discard) set.Remove(cell);
                        if (access.Effect != ResourceEffect.Read && access.Coverage == WriteCoverage.Full) set.Add(cell);
                    }
                }
            }
        }
    }

    private static CompiledRendering?[] BuildRenderingPlans(FrozenGraph graph, QueueType[] queues, GraphLiveness liveness)
    {
        CompiledRendering?[] result = new CompiledRendering?[graph.Passes.Length];
        for (int passIndex = 0; passIndex < graph.Passes.Length; passIndex++)
        {
            FrozenPass pass = graph.Passes[passIndex];
            if (pass.ColorAttachments.Length == 0 && pass.DepthStencilAttachment is null)
            {
                if (pass.Accesses.Any(static access => access.Kind == ResourceNodeKind.Texture &&
                    access.TextureUse is TextureUse.ColorAttachment or TextureUse.DepthRead or TextureUse.DepthWrite))
                    throw new InvalidOperationException($"Pass '{pass.Name}' has an attachment access without specialized attachment metadata.");
                continue;
            }

            if (liveness.Passes[passIndex] && queues[passIndex] != QueueType.Graphics)
                throw new InvalidOperationException($"Pass '{pass.Name}' declares rendering attachments but does not select the graphics queue.");

            int width = 0;
            int height = 0;
            int sampleCount = 0;
            HashSet<int> attachmentAccesses = new();
            for (int colorIndex = 0; colorIndex < pass.ColorAttachments.Length; colorIndex++)
            {
                FrozenColorAttachment color = pass.ColorAttachments[colorIndex];
                if (color.Slot != colorIndex)
                    throw new InvalidOperationException($"Pass '{pass.Name}' color attachment slots must be unique and contiguous starting at zero.");
                if ((uint)color.View >= (uint)graph.TextureViews.Length)
                    throw new InvalidOperationException($"Pass '{pass.Name}' references an invalid color attachment view ordinal.");
                if ((uint)color.Access >= (uint)pass.Accesses.Length)
                    throw new InvalidOperationException($"Pass '{pass.Name}' references an invalid color attachment access ordinal.");
                if (!Enum.IsDefined(color.Load))
                    throw new InvalidOperationException($"Pass '{pass.Name}' has an invalid color attachment load action.");
                if (!attachmentAccesses.Add(color.Access))
                    throw new InvalidOperationException($"Pass '{pass.Name}' reuses one texture access for multiple color attachment slots.");

                FrozenTextureView view = graph.TextureViews[color.View];
                FrozenAccess access = pass.Accesses[color.Access];
                if (access.Kind != ResourceNodeKind.Texture || access.Resource != view.Resource || access.View != color.View ||
                    access.TextureUse != TextureUse.ColorAttachment || access.Effect != ResourceEffect.Write)
                {
                    throw new InvalidOperationException($"Pass '{pass.Name}' color attachment metadata does not match its frozen texture access.");
                }
                PriorContents expectedPrior = color.Load == LoadAction.Load ? PriorContents.Required : PriorContents.Discard;
                WriteCoverage expectedCoverage = color.Load == LoadAction.Clear ? WriteCoverage.Full : WriteCoverage.Partial;
                if (access.PriorContents != expectedPrior || access.Coverage != expectedCoverage)
                    throw new InvalidOperationException($"Pass '{pass.Name}' color attachment access does not match its load operation.");
                if ((view.Usage & TextureViewUsage.ColorAttachment) == 0)
                    throw new InvalidOperationException($"Pass '{pass.Name}' color attachment view lacks ColorAttachment usage.");
                if (view.Range.MipCount != 1 || view.Range.LayerCount != 1 || view.Range.Aspect != TextureAspect.Color)
                    throw new InvalidOperationException($"Pass '{pass.Name}' color attachment views must select exactly one color mip and one array layer.");

                FrozenResource resource = graph.Resources[view.Resource];
                TextureDesc desc = resource.TextureDesc;
                if ((desc.Usage & TextureUsage.ColorAttachment) == 0)
                    throw new InvalidOperationException($"Pass '{pass.Name}' color attachment resource lacks ColorAttachment usage.");
                if (desc.Format is Format.D32Float or Format.D24UNormS8UInt || view.Format != desc.Format)
                    throw new InvalidOperationException($"Pass '{pass.Name}' color attachment requires an exact non-depth view format.");
                if (desc.Depth != 1)
                    throw new NotSupportedException("Three-dimensional color attachments are not part of the current render-graph contract.");

                int mipWidth = Math.Max(1, desc.Width >> view.Range.FirstMip);
                int mipHeight = Math.Max(1, desc.Height >> view.Range.FirstMip);
                if (sampleCount == 0)
                {
                    width = mipWidth;
                    height = mipHeight;
                    sampleCount = desc.SampleCount;
                }
                else if (width != mipWidth || height != mipHeight || sampleCount != desc.SampleCount)
                {
                    throw new InvalidOperationException($"Pass '{pass.Name}' color attachments must have identical extent and sample count.");
                }
            }

            if (pass.DepthStencilAttachment is FrozenDepthStencilAttachment depthStencil)
            {
                if ((uint)depthStencil.View >= (uint)graph.TextureViews.Length)
                    throw new InvalidOperationException($"Pass '{pass.Name}' references an invalid depth-stencil attachment view ordinal.");
                FrozenTextureView view = graph.TextureViews[depthStencil.View];
                if ((view.Usage & TextureViewUsage.DepthStencilAttachment) == 0)
                    throw new InvalidOperationException($"Pass '{pass.Name}' depth-stencil view lacks DepthStencilAttachment usage.");
                if (view.Range.MipCount != 1 || view.Range.LayerCount != 1)
                    throw new InvalidOperationException($"Pass '{pass.Name}' depth-stencil view must select exactly one mip and one array layer.");
                FrozenResource resource = graph.Resources[view.Resource];
                TextureDesc desc = resource.TextureDesc;
                if (desc.Format is not (Format.D32Float or Format.D24UNormS8UInt) || view.Format != desc.Format)
                    throw new InvalidOperationException($"Pass '{pass.Name}' depth-stencil attachment requires an exact depth format.");
                if ((desc.Usage & TextureUsage.DepthStencilAttachment) == 0 || desc.Depth != 1)
                    throw new InvalidOperationException($"Pass '{pass.Name}' depth-stencil attachment resource is not renderable.");

                ValidateDepthStencilPlane(graph, pass, depthStencil, view, depthPlane: true, attachmentAccesses);
                ValidateDepthStencilPlane(graph, pass, depthStencil, view, depthPlane: false, attachmentAccesses);
                if (desc.Format == Format.D32Float && depthStencil.Stencil is not null)
                    throw new InvalidOperationException($"Pass '{pass.Name}' cannot attach a stencil plane to D32Float.");

                int mipWidth = Math.Max(1, desc.Width >> view.Range.FirstMip);
                int mipHeight = Math.Max(1, desc.Height >> view.Range.FirstMip);
                if (sampleCount == 0)
                {
                    width = mipWidth;
                    height = mipHeight;
                    sampleCount = desc.SampleCount;
                }
                else if (width != mipWidth || height != mipHeight || sampleCount != desc.SampleCount)
                {
                    throw new InvalidOperationException($"Pass '{pass.Name}' color and depth-stencil attachments must have identical extent and sample count.");
                }
            }
            for (int accessIndex = 0; accessIndex < pass.Accesses.Length; accessIndex++)
            {
                FrozenAccess access = pass.Accesses[accessIndex];
                if (access.Kind == ResourceNodeKind.Texture &&
                    access.TextureUse is TextureUse.ColorAttachment or TextureUse.DepthRead or TextureUse.DepthWrite &&
                    !attachmentAccesses.Contains(accessIndex))
                {
                    throw new InvalidOperationException($"Pass '{pass.Name}' has an attachment access without specialized attachment metadata.");
                }
            }
            if (liveness.Passes[passIndex]) result[passIndex] = new CompiledRendering(width, height);
        }
        return result;
    }

    private static void ValidateDepthStencilPlane(
        FrozenGraph graph,
        FrozenPass pass,
        in FrozenDepthStencilAttachment attachment,
        in FrozenTextureView view,
        bool depthPlane,
        HashSet<int> attachmentAccesses)
    {
        int accessOrdinal = depthPlane ? attachment.DepthAccess : attachment.StencilAccess;
        bool present = depthPlane ? attachment.Depth is not null : attachment.Stencil is not null;
        TextureAspect aspect = depthPlane ? TextureAspect.Depth : TextureAspect.Stencil;
        if (!present)
        {
            if (accessOrdinal != -1)
                throw new InvalidOperationException($"Pass '{pass.Name}' has an access ordinal for an absent {aspect} attachment plane.");
            return;
        }
        if ((view.Range.Aspect & aspect) == 0 || (uint)accessOrdinal >= (uint)pass.Accesses.Length)
            throw new InvalidOperationException($"Pass '{pass.Name}' has invalid {aspect} attachment metadata.");
        if (!attachmentAccesses.Add(accessOrdinal))
            throw new InvalidOperationException($"Pass '{pass.Name}' reuses one access for multiple attachment planes.");

        LoadAction load;
        bool readOnly;
        if (depthPlane)
        {
            DepthAttachmentOps ops = attachment.Depth!.Value;
            load = ops.Load;
            readOnly = ops.ReadOnly;
        }
        else
        {
            StencilAttachmentOps ops = attachment.Stencil!.Value;
            load = ops.Load;
            readOnly = ops.ReadOnly;
        }

        FrozenAccess access = pass.Accesses[accessOrdinal];
        ResourceEffect expectedEffect = readOnly ? ResourceEffect.Read : ResourceEffect.Write;
        TextureUse expectedUse = readOnly ? TextureUse.DepthRead : TextureUse.DepthWrite;
        PriorContents expectedPrior = readOnly || load == LoadAction.Load ? PriorContents.Required : PriorContents.Discard;
        WriteCoverage expectedCoverage = !readOnly && load == LoadAction.Clear ? WriteCoverage.Full : WriteCoverage.Partial;
        if (access.Kind != ResourceNodeKind.Texture || access.Resource != view.Resource || access.View != attachment.View ||
            access.TextureUse != expectedUse || access.Effect != expectedEffect ||
            access.TextureRange != (view.Range with { Aspect = aspect }) ||
            access.PriorContents != expectedPrior || access.Coverage != expectedCoverage)
        {
            throw new InvalidOperationException($"Pass '{pass.Name}' {aspect} attachment metadata does not match its frozen texture access.");
        }
    }

    private static void ValidateResourceContracts(FrozenGraph graph, QueueType[] queues, GraphLiveness liveness)
    {
        for (int resource = 0; resource < graph.Resources.Length; resource++)
        {
            FrozenResource value = graph.Resources[resource];
            if (!value.IsImported) continue;
            bool used = liveness.Resources[resource];
            if (value.Kind == ResourceNodeKind.Buffer)
            {
                RequireBufferUsage(value.BufferDesc, value.ImportedBuffer.InitialUse, "initial import");
                RequireBufferUsage(value.BufferDesc, value.ImportedBuffer.FinalUse, "final import");
                if (!used && value.ImportedBuffer.InitialUse != value.ImportedBuffer.FinalUse)
                    throw new InvalidOperationException("An unused imported buffer cannot establish a different final use because no submission owns the transition.");
            }
            else
            {
                RequireTextureUsage(value.TextureDesc, value.ImportedTexture.InitialUse, "initial import");
                RequireTextureUsage(value.TextureDesc, value.ImportedTexture.FinalUse, "final import");
                if (!used && value.ImportedTexture.InitialUse != value.ImportedTexture.FinalUse)
                    throw new InvalidOperationException("An unused imported texture cannot establish a different final use because no submission owns the transition.");
            }
        }

        for (int pass = 0; pass < graph.Passes.Length; pass++)
        {
            foreach (FrozenAccess access in graph.Passes[pass].Accesses)
            {
                FrozenResource resource = graph.Resources[access.Resource];
                if (access.Kind == ResourceNodeKind.Buffer)
                {
                    RequireBufferUsage(resource.BufferDesc, access.BufferUse, $"pass '{graph.Passes[pass].Name}'");
                    if (liveness.Passes[pass] && !QueueSupports(queues[pass], access.BufferUse))
                        throw new InvalidOperationException($"Pass '{graph.Passes[pass].Name}' selects {queues[pass]} but declares buffer use {access.BufferUse}.");
                }
                else
                {
                    RequireTextureUsage(resource.TextureDesc, access.TextureUse, $"pass '{graph.Passes[pass].Name}'");
                    if (liveness.Passes[pass] && !QueueSupports(queues[pass], access.TextureUse))
                        throw new InvalidOperationException($"Pass '{graph.Passes[pass].Name}' selects {queues[pass]} but declares texture use {access.TextureUse}.");
                }
                ValidateViewAccess(graph, graph.Passes[pass], access);
            }
        }
    }

    private static void ValidateViewAccess(FrozenGraph graph, FrozenPass pass, in FrozenAccess access)
    {
        if (access.View == -1) return;
        if (access.View < 0) throw new InvalidOperationException($"Pass '{pass.Name}' contains an invalid view ordinal.");
        if (access.Kind == ResourceNodeKind.Buffer)
        {
            if ((uint)access.View >= (uint)graph.BufferViews.Length)
                throw new InvalidOperationException($"Pass '{pass.Name}' references an invalid buffer view ordinal.");
            FrozenBufferView view = graph.BufferViews[access.View];
            BufferUse expected = view.Kind switch
            {
                BindingKind.ConstantBuffer => BufferUse.VertexOrConstant,
                BindingKind.ReadOnlyBuffer => BufferUse.ShaderRead,
                BindingKind.StorageBuffer => BufferUse.ShaderWrite,
                _ => throw new InvalidOperationException($"Pass '{pass.Name}' references a non-buffer binding view."),
            };
            if (view.Resource != access.Resource || view.Range != access.BufferRange || expected != access.BufferUse)
                throw new InvalidOperationException($"Pass '{pass.Name}' buffer view access does not match its frozen view shape.");
            return;
        }

        if ((uint)access.View >= (uint)graph.TextureViews.Length)
            throw new InvalidOperationException($"Pass '{pass.Name}' references an invalid texture view ordinal.");
        FrozenTextureView textureView = graph.TextureViews[access.View];
        bool supportedUse = access.TextureUse switch
        {
            TextureUse.Sampled => (textureView.Usage & TextureViewUsage.ShaderResource) != 0,
            TextureUse.Storage => (textureView.Usage & TextureViewUsage.Storage) != 0,
            TextureUse.ColorAttachment => (textureView.Usage & TextureViewUsage.ColorAttachment) != 0,
            TextureUse.DepthRead or TextureUse.DepthWrite =>
                (textureView.Usage & TextureViewUsage.DepthStencilAttachment) != 0,
            _ => false,
        };
        bool rangeMatches = textureView.Range.FirstMip == access.TextureRange.FirstMip &&
                            textureView.Range.MipCount == access.TextureRange.MipCount &&
                            textureView.Range.FirstLayer == access.TextureRange.FirstLayer &&
                            textureView.Range.LayerCount == access.TextureRange.LayerCount &&
                            (access.TextureRange.Aspect & textureView.Range.Aspect) == access.TextureRange.Aspect;
        if (textureView.Resource != access.Resource || !rangeMatches || !supportedUse)
            throw new InvalidOperationException($"Pass '{pass.Name}' texture view access does not match its frozen view shape.");
    }

    private static void RequireBufferUsage(in BufferDesc desc, BufferUse use, string owner)
    {
        BufferUsage required = use switch
        {
            BufferUse.CopySource => BufferUsage.CopySource,
            BufferUse.CopyDestination => BufferUsage.CopyDestination,
            BufferUse.ShaderRead => BufferUsage.ShaderRead,
            BufferUse.ShaderWrite => BufferUsage.ShaderWrite,
            BufferUse.VertexOrConstant => BufferUsage.Vertex | BufferUsage.Constant,
            BufferUse.Index => BufferUsage.Index,
            BufferUse.Indirect => BufferUsage.Indirect,
            _ => throw new ArgumentOutOfRangeException(nameof(use)),
        };
        bool available = use == BufferUse.VertexOrConstant
            ? (desc.Usage & required) != 0
            : (desc.Usage & required) == required;
        if (!available) throw new InvalidOperationException($"{owner} requires buffer usage {use}, but '{desc.Name ?? "unnamed"}' was created with {desc.Usage}.");
    }

    private static void RequireTextureUsage(in TextureDesc desc, TextureUse use, string owner)
    {
        TextureUsage required = use switch
        {
            TextureUse.CopySource => TextureUsage.CopySource,
            TextureUse.CopyDestination => TextureUsage.CopyDestination,
            TextureUse.ResolveSource => TextureUsage.CopySource,
            TextureUse.ResolveDestination => TextureUsage.CopyDestination,
            TextureUse.Sampled => TextureUsage.Sampled,
            TextureUse.Storage => TextureUsage.Storage,
            TextureUse.ColorAttachment => TextureUsage.ColorAttachment,
            TextureUse.DepthRead or TextureUse.DepthWrite => TextureUsage.DepthStencilAttachment,
            _ => throw new ArgumentOutOfRangeException(nameof(use)),
        };
        if ((desc.Usage & required) != required)
            throw new InvalidOperationException($"{owner} requires texture usage {use}, but '{desc.Name ?? "unnamed"}' was created with {desc.Usage}.");

        bool depth = desc.Format is Format.D24UNormS8UInt or Format.D32Float;
        if (use == TextureUse.ColorAttachment && depth)
            throw new InvalidOperationException($"{owner} cannot use depth format {desc.Format} as a color attachment.");
        if (use is TextureUse.DepthRead or TextureUse.DepthWrite && !depth)
            throw new InvalidOperationException($"{owner} cannot use color format {desc.Format} as a depth attachment.");
        if (use == TextureUse.Storage && depth)
            throw new NotSupportedException("Storage access to depth/stencil formats is not part of the current graphics contract.");
    }

    private static bool QueueSupports(QueueType queue, BufferUse use) => queue switch
    {
        QueueType.Graphics => true,
        QueueType.Compute => use is BufferUse.CopySource or BufferUse.CopyDestination or BufferUse.ShaderRead or BufferUse.ShaderWrite,
        QueueType.Copy => use is BufferUse.CopySource or BufferUse.CopyDestination,
        _ => false,
    };

    private static bool QueueSupports(QueueType queue, TextureUse use) => queue switch
    {
        QueueType.Graphics => true,
        QueueType.Compute => use is TextureUse.CopySource or TextureUse.CopyDestination or TextureUse.Sampled or TextureUse.Storage,
        QueueType.Copy => use is TextureUse.CopySource or TextureUse.CopyDestination,
        _ => false,
    };

    internal static int[][] BuildDependencies(FrozenGraph graph, QueueType[] queues, GraphLiveness liveness)
    {
        int[][] result = new int[graph.Passes.Length][];
        for (int current = 0; current < graph.Passes.Length; current++)
        {
            SortedSet<int> dependencies = new();
            if (!liveness.Passes[current])
            {
                result[current] = [];
                continue;
            }
            FrozenPass pass = graph.Passes[current];
            for (int previous = 0; previous < current; previous++)
            {
                if (!liveness.Passes[previous]) continue;
                FrozenPass prior = graph.Passes[previous];
                bool depends = false;
                foreach (FrozenAccess access in pass.Accesses)
                {
                    foreach (FrozenAccess earlier in prior.Accesses)
                    {
                        if (access.Resource != earlier.Resource || access.Kind != earlier.Kind) continue;
                        bool overlaps = AccessNormalizer.Overlaps(access, earlier);
                        bool hazard = overlaps &&
                                      (access.Effect != ResourceEffect.Read || earlier.Effect != ResourceEffect.Read);
                        bool stateDomainOverlaps = access.Kind == ResourceNodeKind.Buffer || overlaps;
                        bool crossQueueConstraint = queues[current] != queues[previous] && stateDomainOverlaps;
                        if (hazard || crossQueueConstraint)
                        {
                            depends = true;
                            break;
                        }
                    }
                    if (depends) break;
                }
                if (depends) dependencies.Add(previous);
            }
            result[current] = dependencies.ToArray();
        }
        return result;
    }

    private static void BuildBarriers(
        FrozenGraph graph,
        GraphLiveness liveness,
        QueueType[] queues,
        out BarrierTemplate[][] before,
        out BarrierTemplate[][] after,
        out InternalBarrierEdge[] internalBarriers)
    {
        int passCount = graph.Passes.Length;
        before = new BarrierTemplate[passCount][];
        after = new BarrierTemplate[passCount][];
        List<BarrierTemplate>[] beforeLists = Enumerable.Range(0, passCount).Select(static _ => new List<BarrierTemplate>()).ToArray();
        List<BarrierTemplate>[] afterLists = Enumerable.Range(0, passCount).Select(static _ => new List<BarrierTemplate>()).ToArray();
        List<BarrierTemplate>[] internalBefore = Enumerable.Range(0, passCount).Select(static _ => new List<BarrierTemplate>()).ToArray();
        SortedSet<int>[] internalPredecessors = Enumerable.Range(0, passCount).Select(static _ => new SortedSet<int>()).ToArray();
        ResourceState[] bufferStates = new ResourceState[graph.Resources.Length];
        int[] bufferLastPass = Enumerable.Repeat(-1, graph.Resources.Length).ToArray();
        ResourceEffect[] bufferLastEffect = new ResourceEffect[graph.Resources.Length];
        TextureBarrierState?[] textureStates = new TextureBarrierState?[graph.Resources.Length];

        for (int resource = 0; resource < graph.Resources.Length; resource++)
        {
            FrozenResource value = graph.Resources[resource];
            ResourceState initial = InitialState(value);
            if (value.Kind == ResourceNodeKind.Buffer) bufferStates[resource] = initial;
            else textureStates[resource] = new TextureBarrierState(value.TextureDesc, initial);
        }

        for (int pass = 0; pass < passCount; pass++)
        {
            if (!liveness.Passes[pass]) continue;
            foreach (FrozenAccess access in graph.Passes[pass].Accesses)
            {
                ResourceState desired = DesiredState(access);
                if (access.Kind == ResourceNodeKind.Buffer)
                {
                    if (bufferStates[access.Resource] != desired)
                    {
                        BarrierTemplate transition = new(
                            BarrierKind.Transition,
                            access.Resource,
                            bufferStates[access.Resource],
                            desired,
                            default);
                        if (QueueSupportsBarrier(queues[pass], transition.Before, transition.After))
                        {
                            beforeLists[pass].Add(transition);
                        }
                        else
                        {
                            internalBefore[pass].Add(transition);
                            if (bufferLastPass[access.Resource] >= 0)
                                internalPredecessors[pass].Add(bufferLastPass[access.Resource]);
                        }
                        bufferStates[access.Resource] = desired;
                    }
                    else if (desired == ResourceState.UnorderedAccess && bufferLastPass[access.Resource] >= 0 &&
                             (bufferLastEffect[access.Resource] != ResourceEffect.Read || access.Effect != ResourceEffect.Read))
                    {
                        beforeLists[pass].Add(new BarrierTemplate(BarrierKind.UnorderedAccess, access.Resource, desired, desired, default));
                    }
                    bufferLastPass[access.Resource] = pass;
                    bufferLastEffect[access.Resource] = access.Effect;
                    continue;
                }

                FrozenResource resource = graph.Resources[access.Resource];
                TextureBarrierState states = textureStates[access.Resource]!;
                bool requiresUavOrdering = false;
                foreach (TextureCell cell in EnumerateCells(resource.TextureDesc, access.TextureRange))
                {
                    int index = cell.Index(resource.TextureDesc);
                    ResourceState previous = states.States[index];
                    if (previous != desired)
                    {
                        BarrierTemplate transition = new(
                            BarrierKind.Transition,
                            access.Resource,
                            previous,
                            desired,
                            cell.Range);
                        if (QueueSupportsBarrier(queues[pass], transition.Before, transition.After))
                        {
                            beforeLists[pass].Add(transition);
                        }
                        else
                        {
                            internalBefore[pass].Add(transition);
                            if (states.LastPass[index] >= 0)
                                internalPredecessors[pass].Add(states.LastPass[index]);
                        }
                        states.States[index] = desired;
                    }
                    else if (desired == ResourceState.UnorderedAccess && states.LastPass[index] >= 0 &&
                             (states.LastEffect[index] != ResourceEffect.Read || access.Effect != ResourceEffect.Read))
                    {
                        requiresUavOrdering = true;
                    }
                    states.LastPass[index] = pass;
                    states.LastEffect[index] = access.Effect;
                }
                if (requiresUavOrdering)
                    beforeLists[pass].Add(new BarrierTemplate(BarrierKind.UnorderedAccess, access.Resource, desired, desired, access.TextureRange));
            }
        }

        List<BarrierTemplate> finalBarriers = [];
        SortedSet<int> finalPredecessors = [];
        for (int resource = 0; resource < graph.Resources.Length; resource++)
        {
            FrozenResource value = graph.Resources[resource];
            if (!value.IsImported) continue;
            ResourceState final = FinalState(value);
            if (value.Kind == ResourceNodeKind.Buffer)
            {
                if (bufferLastPass[resource] >= 0 && bufferStates[resource] != final)
                {
                    finalBarriers.Add(new BarrierTemplate(
                        BarrierKind.Transition,
                        resource,
                        bufferStates[resource],
                        final,
                        default));
                    AddResourceUsePasses(graph, liveness, resource, finalPredecessors);
                }
                continue;
            }

            TextureBarrierState states = textureStates[resource]!;
            if (!states.LastPass.Any(static pass => pass >= 0)) continue;
            TextureSubresourceRange whole = new(
                0,
                value.TextureDesc.MipLevels,
                0,
                value.TextureDesc.ArrayLayers,
                AspectFor(value.TextureDesc.Format));
            foreach (TextureCell cell in EnumerateCells(value.TextureDesc, whole))
            {
                int index = cell.Index(value.TextureDesc);
                if (states.States[index] == final) continue;
                finalBarriers.Add(new BarrierTemplate(
                    BarrierKind.Transition,
                    resource,
                    states.States[index],
                    final,
                    cell.Range));
            }
            if (finalBarriers.Any(barrier => barrier.Resource == resource))
                AddResourceUsePasses(graph, liveness, resource, finalPredecessors);
        }

        List<InternalBarrierEdge> edges = [];
        for (int pass = 0; pass < passCount; pass++)
        {
            before[pass] = beforeLists[pass].ToArray();
            after[pass] = afterLists[pass].ToArray();
            if (internalBefore[pass].Count != 0)
            {
                edges.Add(new InternalBarrierEdge(
                    internalBefore[pass].ToArray(),
                    internalPredecessors[pass].ToArray(),
                    [pass],
                    pass,
                    pass));
            }
        }
        if (finalBarriers.Count != 0)
        {
            edges.Add(new InternalBarrierEdge(
                finalBarriers.ToArray(),
                finalPredecessors.ToArray(),
                [],
                int.MaxValue,
                int.MaxValue));
        }
        internalBarriers = edges.ToArray();
    }

    private static void AddResourceUsePasses(
        FrozenGraph graph,
        GraphLiveness liveness,
        int resource,
        SortedSet<int> passes)
    {
        foreach (int pass in liveness.ActivePassOrdinals)
        {
            if (graph.Passes[pass].Accesses.Any(access => access.Resource == resource))
                passes.Add(pass);
        }
    }

    private static bool QueueSupportsBarrier(QueueType queue, ResourceState before, ResourceState after) =>
        queue == QueueType.Graphics ||
        QueueSupportsState(queue, before) && QueueSupportsState(queue, after);

    private static bool QueueSupportsState(QueueType queue, ResourceState state) => queue switch
    {
        QueueType.Graphics => Enum.IsDefined(state),
        QueueType.Compute => state is ResourceState.Common or
            ResourceState.CopySource or ResourceState.CopyDestination or
            ResourceState.ShaderResource or ResourceState.UnorderedAccess or
            ResourceState.VertexOrConstantBuffer or ResourceState.IndirectArgument,
        QueueType.Copy => state is ResourceState.Common or
            ResourceState.CopySource or ResourceState.CopyDestination,
        _ => false,
    };

    internal static ResourceState DesiredState(in FrozenAccess access) => access.Kind == ResourceNodeKind.Buffer
        ? access.BufferUse switch
        {
            BufferUse.CopySource => ResourceState.CopySource,
            BufferUse.CopyDestination => ResourceState.CopyDestination,
            BufferUse.ShaderRead => ResourceState.ShaderResource,
            BufferUse.ShaderWrite => ResourceState.UnorderedAccess,
            BufferUse.VertexOrConstant => ResourceState.VertexOrConstantBuffer,
            BufferUse.Index => ResourceState.IndexBuffer,
            BufferUse.Indirect => ResourceState.IndirectArgument,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        }
        : access.TextureUse switch
        {
            TextureUse.CopySource => ResourceState.CopySource,
            TextureUse.CopyDestination => ResourceState.CopyDestination,
            TextureUse.ResolveSource => ResourceState.ResolveSource,
            TextureUse.ResolveDestination => ResourceState.ResolveDestination,
            TextureUse.Sampled => ResourceState.ShaderResource,
            TextureUse.Storage => ResourceState.UnorderedAccess,
            TextureUse.ColorAttachment => ResourceState.RenderTarget,
            TextureUse.DepthRead => ResourceState.DepthRead,
            TextureUse.DepthWrite => ResourceState.DepthWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };

    private static ResourceState InitialState(in FrozenResource resource) => resource.Kind == ResourceNodeKind.Buffer
        ? resource.IsImported ? Map(resource.ImportedBuffer.InitialUse) : ResourceState.Common
        : resource.IsImported ? Map(resource.ImportedTexture.InitialUse) : ResourceState.Common;

    private static ResourceState FinalState(in FrozenResource resource) => resource.Kind == ResourceNodeKind.Buffer
        ? Map(resource.ImportedBuffer.FinalUse)
        : Map(resource.ImportedTexture.FinalUse);

    private static ResourceState Map(BufferUse use) => use switch
    {
        BufferUse.CopySource => ResourceState.CopySource,
        BufferUse.CopyDestination => ResourceState.CopyDestination,
        BufferUse.ShaderRead => ResourceState.ShaderResource,
        BufferUse.ShaderWrite => ResourceState.UnorderedAccess,
        BufferUse.VertexOrConstant => ResourceState.VertexOrConstantBuffer,
        BufferUse.Index => ResourceState.IndexBuffer,
        BufferUse.Indirect => ResourceState.IndirectArgument,
        _ => throw new ArgumentOutOfRangeException(nameof(use)),
    };

    private static ResourceState Map(TextureUse use) => use switch
    {
        TextureUse.CopySource => ResourceState.CopySource,
        TextureUse.CopyDestination => ResourceState.CopyDestination,
        TextureUse.ResolveSource => ResourceState.ResolveSource,
        TextureUse.ResolveDestination => ResourceState.ResolveDestination,
        TextureUse.Sampled => ResourceState.ShaderResource,
        TextureUse.Storage => ResourceState.UnorderedAccess,
        TextureUse.ColorAttachment => ResourceState.RenderTarget,
        TextureUse.DepthRead => ResourceState.DepthRead,
        TextureUse.DepthWrite => ResourceState.DepthWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(use)),
    };

    private static TextureAspect AspectFor(Format format) => format switch
    {
        Format.D32Float => TextureAspect.Depth,
        Format.D24UNormS8UInt => TextureAspect.Depth | TextureAspect.Stencil,
        _ => TextureAspect.Color,
    };

    private static IEnumerable<TextureCell> EnumerateCells(TextureDesc desc, TextureSubresourceRange range)
    {
        TextureAspect[] aspects = [TextureAspect.Color, TextureAspect.Depth, TextureAspect.Stencil];
        for (int layer = range.FirstLayer; layer < range.FirstLayer + range.LayerCount; layer++)
        for (int mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
        foreach (TextureAspect aspect in aspects)
            if ((range.Aspect & aspect) != 0)
                yield return new TextureCell(mip, layer, aspect);
    }

    private sealed class TextureBarrierState
    {
        public TextureBarrierState(in TextureDesc desc, ResourceState initial)
        {
            int planes = desc.Format == Format.D24UNormS8UInt ? 2 : 1;
            int count = checked(desc.MipLevels * desc.ArrayLayers * planes);
            States = Enumerable.Repeat(initial, count).ToArray();
            LastPass = Enumerable.Repeat(-1, count).ToArray();
            LastEffect = new ResourceEffect[count];
        }

        public ResourceState[] States { get; }
        public int[] LastPass { get; }
        public ResourceEffect[] LastEffect { get; }
    }

    private readonly record struct TextureCell(int Mip, int Layer, TextureAspect Aspect)
    {
        public TextureSubresourceRange Range => new(Mip, 1, Layer, 1, Aspect);
        public int Index(in TextureDesc desc)
        {
            int plane = Aspect == TextureAspect.Stencil ? 1 : 0;
            return checked(Mip + Layer * desc.MipLevels + plane * desc.MipLevels * desc.ArrayLayers);
        }
    }
}

internal readonly record struct InternalBarrierEdge(
    BarrierTemplate[] Barriers,
    int[] PredecessorPasses,
    int[] SuccessorPasses,
    int SortPass,
    int StableOrdinal);

internal sealed class IntervalSet
{
    private readonly List<(ulong Start, ulong End)> _intervals = new();

    public void Add(ulong start, ulong end)
    {
        if (start >= end) throw new ArgumentOutOfRangeException(nameof(end));
        int index = 0;
        while (index < _intervals.Count && _intervals[index].End < start) index++;
        while (index < _intervals.Count && _intervals[index].Start <= end)
        {
            start = Math.Min(start, _intervals[index].Start);
            end = Math.Max(end, _intervals[index].End);
            _intervals.RemoveAt(index);
        }
        _intervals.Insert(index, (start, end));
    }

    public bool Contains(ulong start, ulong end)
    {
        foreach ((ulong candidateStart, ulong candidateEnd) in _intervals)
        {
            if (candidateStart > start) return false;
            if (candidateStart <= start && candidateEnd >= end) return true;
        }
        return false;
    }

    public void Remove(ulong start, ulong end)
    {
        if (start >= end) throw new ArgumentOutOfRangeException(nameof(end));
        for (int index = 0; index < _intervals.Count;)
        {
            (ulong existingStart, ulong existingEnd) = _intervals[index];
            if (existingEnd <= start)
            {
                index++;
                continue;
            }
            if (existingStart >= end) break;

            _intervals.RemoveAt(index);
            if (existingStart < start)
            {
                _intervals.Insert(index, (existingStart, start));
                index++;
            }
            if (existingEnd > end)
            {
                _intervals.Insert(index, (end, existingEnd));
                break;
            }
        }
    }
}
