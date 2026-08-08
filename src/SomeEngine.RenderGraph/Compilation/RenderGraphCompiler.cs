namespace SomeEngine.RenderGraph;

using System.Diagnostics;
using System.Runtime.CompilerServices;

internal static partial class RenderGraphCompiler
{
    internal const ulong SemanticGeneration = 6;
    private const int SplitBarrierMinimumPassDistance = 9;

    internal static QueueType SelectQueue(
        QueueType queue,
        IGraphicsBackend backend,
        Device device)
    {
        if (!Enum.IsDefined(queue)) throw new ArgumentOutOfRangeException(nameof(queue));
        _ = backend.GetQueue(device, queue);
        return queue;
    }

    private static QueueType RequireQueue(QueueType queue) =>
        Enum.IsDefined(queue) ? queue : throw new ArgumentOutOfRangeException(nameof(queue));

    internal static void Compile(
        RenderGraph graph,
        IGraphicsBackend backend,
        Device device)
        => Compile(graph, backend, device, collectTimings: false, out _);

    internal static void Compile(
        RenderGraph graph,
        IGraphicsBackend backend,
        Device device,
        bool collectTimings,
        out CompilerCpuTimings timings)
        => CompileCore(
            graph,
            backend,
            device,
            collectTimings,
            useReferenceDependencyAndBarrierTraversal: false,
            out timings);

    internal static void CompileReference(
        RenderGraph graph,
        IGraphicsBackend backend,
        Device device)
        => CompileCore(
            graph,
            backend,
            device,
            collectTimings: false,
            useReferenceDependencyAndBarrierTraversal: true,
            out _);

    private static unsafe void CompileCore(
        RenderGraph graph,
        IGraphicsBackend backend,
        Device device,
        bool collectTimings,
        bool useReferenceDependencyAndBarrierTraversal,
        out CompilerCpuTimings timings)
    {
        long started = collectTimings ? Stopwatch.GetTimestamp() : 0;
        timings = default;
        graph.MakeCanonicalSlicesContiguous();
        ResetDerivedRows(graph);
        BuildResourceRequirements(graph, backend, device);
        long contentsValidated = collectTimings ? Stopwatch.GetTimestamp() : 0;
        int passCount = graph.Passes.Length;
        PassData* canonicalPassRows =
            graph.Passes.DangerousContiguousPointer;
        PassInputData* canonicalPassInputs =
            graph.PassInputs.DangerousContiguousPointer;
        ArenaSlice<QueueType> queues =
            graph.AllocateSlice<QueueType>(passCount, clear: false);
        for (int pass = 0; pass < passCount; pass++)
        {
            QueueType queue = canonicalPassRows is not null
                ? canonicalPassRows[pass].Queue
                : graph.Passes[pass].Queue;
            queues[pass] = RequireQueue(queue);
        }

        ArenaSlice<int> accessPassOrdinals;
        ArenaSlice<int> resourceAccessOffsets;
        ArenaSlice<int> resourceAccessOrdinals;
        if (useReferenceDependencyAndBarrierTraversal)
        {
            AnalyzeLivenessReference(
                graph,
                out accessPassOrdinals,
                out resourceAccessOffsets,
                out resourceAccessOrdinals);
        }
        else
        {
            AnalyzeLiveness(
                graph,
                out accessPassOrdinals,
                out resourceAccessOffsets,
                out resourceAccessOrdinals);
        }
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        foreach (int pass in graph.ActivePassOrdinals)
            queues[pass] = SelectQueue(queues[pass], backend, device);
        long livenessAnalyzed = collectTimings ? Stopwatch.GetTimestamp() : 0;

        ValidateResourceUsage(graph);
        int descriptorCount = 0;
        int pushConstantCount = 0;
        int accessBucketCount = 0;
        int bindlessBucketCount = 0;
        int queryBucketCount = 0;
        for (int pass = 0; pass < passCount; pass++)
        {
            PassData row = canonicalPassRows is not null
                ? canonicalPassRows[pass]
                : graph.Passes[pass];
            ReadOnlySpan<PassInputData> passAccesses =
                canonicalPassInputs is not null
                    ? new ReadOnlySpan<PassInputData>(
                        canonicalPassInputs + row.AccessOffset,
                        row.AccessCount)
                    : graph.GetPassAccesses(row);
            if (useReferenceDependencyAndBarrierTraversal)
            {
                foreach (ref readonly PassInputData access in passAccesses)
                    ValidatePassAccessUsage(graph, pass, queues[pass], access);
            }
            else if ((liveFlags[pass] &
                      RenderGraph.PassLiveFlag) != 0)
            {
                foreach (ref readonly PassInputData access in passAccesses)
                    ValidatePassAccessQueueUsage(graph, pass, queues[pass], access);
            }
            if ((liveFlags[pass] &
                 RenderGraph.PassLiveFlag) == 0)
            {
                continue;
            }
            descriptorCount = checked(descriptorCount + graph.GetPassDescriptorCount(pass));
            pushConstantCount = checked(pushConstantCount + graph.GetPassPushConstantCount(pass));
            accessBucketCount = checked(
                accessBucketCount + RenderGraph.PassLookupCapacity(row.AccessCount));
            bindlessBucketCount = checked(
                bindlessBucketCount + RenderGraph.PassLookupCapacity(row.BindlessAccessCount));
            queryBucketCount = checked(
                queryBucketCount + RenderGraph.PassLookupCapacity(row.QueryAccessCount));
        }
        graph.DescriptorGroups =
            graph.AllocateSlice<uint>(descriptorCount, clear: false);
        graph.DescriptorWriteOffsets =
            graph.AllocateSlice<int>(descriptorCount, clear: false);
        graph.DescriptorWriteCounts =
            graph.AllocateSlice<int>(descriptorCount, clear: false);
        graph.DescriptorGroupLeaders =
            graph.AllocateSlice<byte>(descriptorCount);
        int indexBucketCount = checked(accessBucketCount + bindlessBucketCount + queryBucketCount);
        ArenaSlice<int> indexBuckets = graph.AllocateSlice<int>(indexBucketCount, clear: false);
        graph.AccessIndexBuckets = indexBuckets.Slice(0, accessBucketCount);
        graph.BindlessAccessIndexBuckets =
            indexBuckets.Slice(accessBucketCount, bindlessBucketCount);
        graph.QueryIndexBuckets = indexBuckets.Slice(
            checked(accessBucketCount + bindlessBucketCount),
            queryBucketCount);
        int descriptorCursor = 0;
        int pushConstantCursor = 0;
        int accessBucketCursor = 0;
        int bindlessBucketCursor = 0;
        int queryBucketCursor = 0;
        foreach (int pass in graph.ActivePassOrdinals)
        {
            PassData row = canonicalPassRows is not null
                ? canonicalPassRows[pass]
                : graph.Passes[pass];
            int passDescriptorCount = graph.GetPassDescriptorCount(pass);
            int passPushConstantCount = graph.GetPassPushConstantCount(pass);
            int passAccessBuckets = RenderGraph.PassLookupCapacity(row.AccessCount);
            int passBindlessBuckets = RenderGraph.PassLookupCapacity(row.BindlessAccessCount);
            int passQueryBuckets = RenderGraph.PassLookupCapacity(row.QueryAccessCount);
            graph.MaterializePassCompilationStorage(
                pass,
                row,
                descriptorCursor,
                pushConstantCursor,
                accessBucketCursor,
                bindlessBucketCursor,
                queryBucketCursor);
            descriptorCursor = checked(descriptorCursor + passDescriptorCount);
            pushConstantCursor = checked(pushConstantCursor + passPushConstantCount);
            accessBucketCursor = checked(accessBucketCursor + passAccessBuckets);
            bindlessBucketCursor = checked(bindlessBucketCursor + passBindlessBuckets);
            queryBucketCursor = checked(queryBucketCursor + passQueryBuckets);
        }
        ArenaSlice<Extent2D> rendering = BuildExtent2Ds(graph, queues);
        long validationBuilt = collectTimings ? Stopwatch.GetTimestamp() : 0;
        PassBarrierTable commandUnitBarriers;
        PassPredecessorTable commandUnitBarrierPredecessors;
        long dependenciesBuilt;
        long barriersBuilt;
        if (useReferenceDependencyAndBarrierTraversal)
        {
            BuildDependenciesReference(graph, queues);
            dependenciesBuilt = collectTimings ? Stopwatch.GetTimestamp() : 0;
            BuildBarriersReference(
                graph,
                device,
                queues,
                out commandUnitBarriers,
                out commandUnitBarrierPredecessors);
            barriersBuilt = collectTimings ? Stopwatch.GetTimestamp() : 0;
        }
        else
        {
            BuildResourceIndexedDependenciesAndBarriers(
                graph,
                device,
                queues,
                accessPassOrdinals,
                resourceAccessOffsets,
                resourceAccessOrdinals,
                out commandUnitBarriers,
                out commandUnitBarrierPredecessors);
            dependenciesBuilt = collectTimings ? Stopwatch.GetTimestamp() : 0;
            barriersBuilt = dependenciesBuilt;
        }
        ReachabilityTable reachability =
            new(graph, graph.ActivePassOrdinals, queues);
        ArenaSlice<PlannedAliasingBarrier> aliasAcquires = TransientPlacementCompiler.Place(
            graph,
            reachability,
            enableAliasing: true,
            accessPassOrdinals,
            resourceAccessOffsets,
            resourceAccessOrdinals);
        ArenaColumn<int> logicalPassRows = default;
        ArenaColumn<int> logicalPassStarts = default;
        RasterScopeCompiler.Group(
            graph,
            graph.ActivePassOrdinals.ReadOnlySpan,
            queues,
            rendering,
            aliasAcquires,
            reachability,
            out logicalPassRows,
            out logicalPassStarts,
            out RasterStatistics raster);
        long placementBuilt = collectTimings ? Stopwatch.GetTimestamp() : 0;
        ArenaSlice<int> passToCommandUnit;
        BuildExecution(
            graph,
            queues,
            logicalPassRows,
            logicalPassStarts,
            aliasAcquires,
            commandUnitBarriers,
            commandUnitBarrierPredecessors,
            useReferenceDependencyAndBarrierTraversal,
            out passToCommandUnit);
        BuildBatchResourcesAndExternalWaits(graph);
        long executionBuilt = collectTimings ? Stopwatch.GetTimestamp() : 0;
        timings = collectTimings
            ? new CompilerCpuTimings(
                Stopwatch.GetElapsedTime(started, contentsValidated),
                Stopwatch.GetElapsedTime(contentsValidated, livenessAnalyzed),
                Stopwatch.GetElapsedTime(livenessAnalyzed, validationBuilt),
                Stopwatch.GetElapsedTime(validationBuilt, dependenciesBuilt),
                Stopwatch.GetElapsedTime(dependenciesBuilt, barriersBuilt),
                Stopwatch.GetElapsedTime(barriersBuilt, placementBuilt),
                Stopwatch.GetElapsedTime(placementBuilt, executionBuilt))
            : default;
        graph.Queues = queues;
        graph.PassToCommandUnit = passToCommandUnit;
        graph.Raster = raster;
        graph.Rendering = rendering;
    }

    private static unsafe void BuildResourceRequirements(
        RenderGraph graph,
        IGraphicsBackend backend,
        Device device)
    {
        int bufferCount = graph.Buffers.Length;
        int textureCount = graph.Textures.Length;
        graph.ResourceRequirementRows = graph.AllocateSlice<GraphMemoryRequirements>(
            graph.ResourceCount,
            clear: false);
        GraphMemoryRequirements* requirements =
            graph.ResourceRequirementRows.DangerousPointer;
        ResourceUnversionedData* canonicalBufferRows =
            graph.Buffers.DangerousContiguousPointer;
        ResourceUnversionedData* canonicalTextureRows =
            graph.Textures.DangerousContiguousPointer;
        for (int index = 0; index < bufferCount; index++)
        {
            ResourceUnversionedData buffer = canonicalBufferRows is not null
                ? canonicalBufferRows[index]
                : graph.Buffers[index];
            if (buffer.IsImported)
            {
                requirements[index] = default;
                continue;
            }
            MemoryRequirements native = backend.GetBufferMemoryRequirements(
                device,
                graph.GetBufferDescription(index),
                buffer.MemoryType);
            requirements[index] = new GraphMemoryRequirements(
                native.Size,
                native.Alignment,
                buffer.MemoryType,
                native.CompatibleHeapFlags);
        }
        for (int index = 0; index < textureCount; index++)
        {
            ResourceUnversionedData texture = canonicalTextureRows is not null
                ? canonicalTextureRows[index]
                : graph.Textures[index];
            int resource = bufferCount + index;
            if (texture.IsImported)
            {
                requirements[resource] = default;
                continue;
            }
            GraphTextureDescription graphDescription = graph.GetTextureDescription(index);
            TextureDesc description = graphDescription.ToRhiDescription();
            MemoryRequirements native = backend.GetTextureMemoryRequirements(device, description);
            requirements[resource] = new GraphMemoryRequirements(
                native.Size,
                native.Alignment,
                MemoryType.DeviceLocal,
                native.CompatibleHeapFlags);
        }
    }

    private static void ResetDerivedRows(RenderGraph graph)
    {
        graph.DependencyRows.Clear();
        graph.BeforeResourceBarriers.Clear();
        graph.AfterResourceBarriers.Clear();
        graph.CommandBatches.Clear();
        graph.CommandUnits.Clear();
        graph.BatchDependencyRows.Clear();
        graph.BatchRuntimeCmds.Clear();
        graph.BatchResourceRows.Clear();
        graph.BatchExternalWaitRows.Clear();
        graph.CommandUnitDependencyRows.Clear();
        graph.CommandUnitPassRows.Clear();
        graph.CommandUnitAliasRows.Clear();
        graph.CommandUnitResourceBarriers.Clear();
        int accessCount = graph.PassInputs.Length;
        graph.DependencyRows.EnsureCapacity(accessCount);
        graph.BeforeResourceBarriers.EnsureCapacity(accessCount);
    }

    private static void BuildExecution(
        RenderGraph graph,
        ArenaSlice<QueueType> queues,
        ArenaColumn<int> logicalPassRows,
        ArenaColumn<int> logicalPassStarts,
        ArenaSlice<PlannedAliasingBarrier> aliasAcquires,
        PassBarrierTable commandUnitBarriers,
        PassPredecessorTable commandUnitBarrierPredecessors,
        bool useReferenceTraversal,
        out ArenaSlice<int> passToCommandUnit)
    {
        if (TryBuildLinearExecution(
                graph,
                queues,
                logicalPassRows,
                logicalPassStarts,
                aliasAcquires,
                commandUnitBarriers,
                out passToCommandUnit))
            return;
        if (TryBuildOrderedSingleQueueExecution(
                graph,
                queues,
                logicalPassRows,
                logicalPassStarts,
                aliasAcquires,
                commandUnitBarriers,
                commandUnitBarrierPredecessors,
                out passToCommandUnit))
        {
            return;
        }

        int logicalPassGroupCount = logicalPassStarts.Count - 1;
        int barrierUnitCount = commandUnitBarriers.NonEmptyKeyCount;
        int unitCount = checked(logicalPassGroupCount + aliasAcquires.Length + barrierUnitCount);
        graph.CommandUnits.EnsureCapacity(checked(graph.CommandUnits.Count + unitCount));
        ArenaSlice<int> passToBuildUnit = graph.AllocateSlice<int>(graph.Passes.Length, clear: false);
        passToBuildUnit.Span.Fill(-1);
        int creationOrdinal = 0;
        for (int groupOrdinal = 0; groupOrdinal < logicalPassGroupCount; groupOrdinal++)
        {
            int offset = logicalPassStarts[groupOrdinal];
            int count = logicalPassStarts[groupOrdinal + 1] - offset;
            ReadOnlySpan<int> passes =
                logicalPassRows.GetReadOnlySpan(offset, count);
            if (passes.IsEmpty) throw new InvalidOperationException("A logical record group cannot be empty.");
            QueueType queue = queues[passes[0]];
            graph.CommandUnits.Add(new RuntimeCmd(
                queue,
                passes.Length > 1 ? RuntimeCmd.RasterScopeCmdId : RuntimeCmd.StandaloneCmdId,
                offset,
                count,
                -1,
                passes[0],
                passes[0],
                creationOrdinal++,
                0,
                0,
                0,
                0));
            foreach (int pass in passes)
            {
                if (queues[pass] != queue || passToBuildUnit[pass] >= 0)
                    throw new InvalidOperationException("Logical record groups must uniquely contain same-queue passes.");
                passToBuildUnit[pass] = groupOrdinal;
            }
        }

        aliasAcquires.Span.Sort(static (left, right) =>
        {
            int order = left.StartPasses[0].CompareTo(right.StartPasses[0]);
            return order != 0 ? order : left.AfterResource.CompareTo(right.AfterResource);
        });
        int aliasBase = logicalPassGroupCount;
        for (int alias = 0; alias < aliasAcquires.Length; alias++)
        {
            PlannedAliasingBarrier edge = aliasAcquires[alias];
            graph.CommandUnits.Add(new RuntimeCmd(
                QueueType.Graphics,
                RuntimeCmd.AliasingBarrierCmdId,
                0,
                0,
                alias,
                edge.StartPasses[0],
                edge.AfterResource,
                creationOrdinal++,
                0,
                0,
                0,
                0));
        }

        int barrierBase = aliasBase + aliasAcquires.Length;
        ArenaSlice<int> barrierKeyToBuildUnit =
            graph.AllocateSlice<int>(commandUnitBarriers.KeyCount, clear: false);
        barrierKeyToBuildUnit.Span.Fill(-1);
        int barrierOrdinal = 0;
        for (int barrierKey = 0; barrierKey < commandUnitBarriers.KeyCount; barrierKey++)
        {
            if (commandUnitBarriers.GetCount(barrierKey) == 0) continue;
            int unit = checked(barrierBase + barrierOrdinal++);
            barrierKeyToBuildUnit[barrierKey] = unit;
            int sortPass = barrierKey == graph.Passes.Length ? int.MaxValue : barrierKey;
            graph.CommandUnits.Add(new RuntimeCmd(
                QueueType.Graphics,
                RuntimeCmd.BarrierCmdId,
                0,
                0,
                barrierKey,
                sortPass,
                sortPass,
                creationOrdinal++,
                0,
                0,
                0,
                0));
        }

        ArenaSlice<ulong> edges = default;
        ArenaSlice<int> incomingOffsets = default;
        ArenaSlice<int> incomingRows = default;
        ArenaSlice<int> successorOffsets = default;
        ArenaSlice<int> successorRows = default;
        ArenaSlice<int> unitOrder = default;
        int sparseEdgeCount = 0;
        ArenaSlice<int> orderedBuildUnits;
        if (useReferenceTraversal)
        {
            int edgeBitCount = checked(unitCount * unitCount);
            edges = graph.AllocateSlice<ulong>((edgeBitCount + 63) / 64);
            ArenaSlice<int> indegrees = graph.AllocateSlice<int>(unitCount);
            for (int pass = 0; pass < passToBuildUnit.Length; pass++)
            {
                int unit = passToBuildUnit[pass];
                if (unit < 0) continue;
                foreach (int predecessor in graph.GetPassDependencies(pass))
                {
                    int predecessorUnit = passToBuildUnit[predecessor];
                    if (predecessorUnit < 0)
                        throw new InvalidOperationException("A live pass depends on a culled pass.");
                    AddUnitDependency(edges, indegrees, unitCount, predecessorUnit, unit);
                }
            }

            ArenaSlice<int> previousOnQueue = graph.AllocateSlice<int>(3, clear: false);
            previousOnQueue.Span.Fill(-1);
            for (int groupOrdinal = 0; groupOrdinal < logicalPassGroupCount; groupOrdinal++)
            {
                RuntimeCmd unit = graph.CommandUnits[groupOrdinal];
                int queue = (int)unit.Queue;
                if (previousOnQueue[queue] >= 0)
                    AddUnitDependency(edges, indegrees, unitCount, previousOnQueue[queue], groupOrdinal);
                previousOnQueue[queue] = groupOrdinal;
            }

            for (int alias = 0; alias < aliasAcquires.Length; alias++)
            {
                int unit = aliasBase + alias;
                PlannedAliasingBarrier edge = aliasAcquires[alias];
                foreach (int predecessor in edge.EndPasses)
                    AddUnitDependency(edges, indegrees, unitCount, passToBuildUnit[predecessor], unit);
                foreach (int successor in edge.StartPasses)
                    AddUnitDependency(edges, indegrees, unitCount, unit, passToBuildUnit[successor]);
            }
            for (int barrierKey = 0; barrierKey < commandUnitBarriers.KeyCount; barrierKey++)
            {
                int unit = barrierKeyToBuildUnit[barrierKey];
                if (unit < 0) continue;
                foreach (int predecessor in commandUnitBarrierPredecessors.CopyToSlice(graph, barrierKey))
                    AddUnitDependency(edges, indegrees, unitCount, passToBuildUnit[predecessor], unit);
                if (barrierKey < graph.Passes.Length)
                    AddUnitDependency(
                        edges,
                        indegrees,
                        unitCount,
                        unit,
                        passToBuildUnit[barrierKey]);
            }

            orderedBuildUnits = graph.AllocateSlice<int>(unitCount, clear: false);
            ArenaSlice<byte> emitted = graph.AllocateSlice<byte>(unitCount);
            for (int ordinal = 0; ordinal < unitCount; ordinal++)
            {
                int selected = -1;
                for (int candidate = 0; candidate < unitCount; candidate++)
                {
                    if (emitted[candidate] != 0 || indegrees[candidate] != 0) continue;
                    if (selected < 0 ||
                        CompareUnit(graph.CommandUnits[candidate], graph.CommandUnits[selected]) < 0)
                        selected = candidate;
                }
                if (selected < 0)
                    throw new InvalidOperationException("The command-unit dependency graph contains a cycle.");
                emitted[selected] = 1;
                orderedBuildUnits[ordinal] = selected;
                for (int successor = 0; successor < unitCount; successor++)
                    if (HasUnitDependency(edges, unitCount, selected, successor)) indegrees[successor]--;
            }
        }
        else
        {
            BuildSparseUnitOrdering(
                graph,
                logicalPassGroupCount,
                aliasAcquires,
                aliasBase,
                commandUnitBarriers,
                commandUnitBarrierPredecessors,
                barrierKeyToBuildUnit,
                passToBuildUnit,
                unitCount,
                out orderedBuildUnits,
                out unitOrder,
                out incomingOffsets,
                out incomingRows,
                out successorOffsets,
                out successorRows,
                out sparseEdgeCount);
        }

        passToCommandUnit = graph.AllocateSlice<int>(graph.Passes.Length, clear: false);
        passToCommandUnit.Span.Fill(-1);
        graph.CommandUnitPassRows.EnsureCapacity(
            checked(graph.CommandUnitPassRows.Count + logicalPassRows.Count));
        graph.CommandUnitAliasRows.EnsureCapacity(
            checked(graph.CommandUnitAliasRows.Count + aliasAcquires.Length));
        graph.CommandUnitResourceBarriers.EnsureCapacity(
            checked(graph.CommandUnitResourceBarriers.Count + commandUnitBarriers.EntryCount));
        for (int orderedUnit = 0; orderedUnit < unitCount; orderedUnit++)
        {
            int unitOrdinal = orderedBuildUnits[orderedUnit];
            RuntimeCmd unit = graph.CommandUnits[unitOrdinal];
            if (unit.CmdId == RuntimeCmd.BarrierCmdId)
            {
                MaterializeBarrierCommandUnit(
                    graph,
                    unitOrdinal,
                    ref commandUnitBarriers,
                    unit.PayloadOrdinal);
                continue;
            }
            ReadOnlySpan<int> passes = unit.PassCount == 0
                ? []
                : logicalPassRows.GetReadOnlySpan(unit.PassOffset, unit.PassCount);
            PlannedAliasingBarrier? alias = unit.CmdId == RuntimeCmd.AliasingBarrierCmdId
                ? new PlannedAliasingBarrier(
                    aliasAcquires[unit.PayloadOrdinal].BeforeResource,
                    aliasAcquires[unit.PayloadOrdinal].AfterResource,
                    default,
                    default)
                : null;
            MaterializeCommandUnit(graph, unitOrdinal, passes, alias);
            foreach (int pass in passes) passToCommandUnit[pass] = unitOrdinal;
        }

        if (useReferenceTraversal)
        {
            for (int orderedUnit = 0; orderedUnit < unitCount; orderedUnit++)
            {
                int dependencyOffset = graph.CommandUnitDependencyRows.Count;
                int unitOrdinal = orderedBuildUnits[orderedUnit];
                int dependencyCount = 0;
                for (int predecessorOrder = 0; predecessorOrder < orderedUnit; predecessorOrder++)
                {
                    int predecessor = orderedBuildUnits[predecessorOrder];
                    if (HasUnitDependency(edges, unitCount, predecessor, unitOrdinal)) dependencyCount++;
                }
                graph.CommandUnitDependencyRows.EnsureAppendCapacity(dependencyCount);
                for (int predecessorOrder = 0; predecessorOrder < orderedUnit; predecessorOrder++)
                {
                    int predecessor = orderedBuildUnits[predecessorOrder];
                    if (HasUnitDependency(edges, unitCount, predecessor, unitOrdinal))
                        graph.CommandUnitDependencyRows.Add(predecessor);
                }
                graph.CommandUnits[unitOrdinal] = graph.CommandUnits[unitOrdinal] with
                {
                    DependencyOffset = dependencyOffset,
                    DependencyCount = graph.CommandUnitDependencyRows.Count - dependencyOffset,
                };
            }
        }
        else
        {
            graph.CommandUnitDependencyRows.EnsureAppendCapacity(sparseEdgeCount);
            for (int orderedUnit = 0; orderedUnit < unitCount; orderedUnit++)
            {
                int unitOrdinal = orderedBuildUnits[orderedUnit];
                Span<int> dependencies = incomingRows.Span.Slice(
                    incomingOffsets[unitOrdinal],
                    incomingOffsets[unitOrdinal + 1] - incomingOffsets[unitOrdinal]);
                SortDependenciesByUnitOrder(dependencies, unitOrder);
                int dependencyOffset = graph.CommandUnitDependencyRows.Count;
                dependencies.CopyTo(
                    graph.CommandUnitDependencyRows.AddUninitialized(dependencies.Length));
                graph.CommandUnits[unitOrdinal] = graph.CommandUnits[unitOrdinal] with
                {
                    DependencyOffset = dependencyOffset,
                    DependencyCount = dependencies.Length,
                };
            }
        }

        if (useReferenceTraversal)
        {
            BuildCommandBatches(
                graph,
                graph.CommandUnits,
                orderedBuildUnits,
                edges);
        }
        else
        {
            BuildCommandBatchesSparse(
                graph,
                graph.CommandUnits,
                orderedBuildUnits,
                incomingOffsets,
                incomingRows,
                successorOffsets,
                successorRows);
        }
    }

    private static bool TryBuildOrderedSingleQueueExecution(
        RenderGraph graph,
        ArenaSlice<QueueType> queues,
        ArenaColumn<int> logicalPassRows,
        ArenaColumn<int> logicalPassStarts,
        ArenaSlice<PlannedAliasingBarrier> aliasAcquires,
        PassBarrierTable commandUnitBarriers,
        PassPredecessorTable commandUnitBarrierPredecessors,
        out ArenaSlice<int> passToCommandUnit)
    {
        passToCommandUnit = default;
        int logicalPassGroupCount = logicalPassStarts.Count - 1;
        if (logicalPassGroupCount == 0) return false;
        ReadOnlySpan<int> firstGroup =
            GetPasses(logicalPassRows, logicalPassStarts, 0);
        if (firstGroup.IsEmpty) throw new InvalidOperationException("A logical record group cannot be empty.");
        QueueType queue = queues[firstGroup[0]];
        int barrierUnitCount = commandUnitBarriers.NonEmptyKeyCount;
        if ((aliasAcquires.Length != 0 || barrierUnitCount != 0) && queue != QueueType.Graphics)
            return false;
        for (int groupOrdinal = 0; groupOrdinal < logicalPassGroupCount; groupOrdinal++)
        {
            ReadOnlySpan<int> passes =
                GetPasses(logicalPassRows, logicalPassStarts, groupOrdinal);
            if (passes.IsEmpty) throw new InvalidOperationException("A logical record group cannot be empty.");
            foreach (int pass in passes)
                if (queues[pass] != queue) return false;
        }

        int unitCount = checked(logicalPassGroupCount + aliasAcquires.Length + barrierUnitCount);
        graph.CommandUnits.EnsureCapacity(checked(graph.CommandUnits.Count + unitCount));
        int creationOrdinal = 0;
        for (int groupOrdinal = 0; groupOrdinal < logicalPassGroupCount; groupOrdinal++)
        {
            int offset = logicalPassStarts[groupOrdinal];
            int count = logicalPassStarts[groupOrdinal + 1] - offset;
            ReadOnlySpan<int> passes =
                logicalPassRows.GetReadOnlySpan(offset, count);
            graph.CommandUnits.Add(new RuntimeCmd(
                queue,
                passes.Length > 1 ? RuntimeCmd.RasterScopeCmdId : RuntimeCmd.StandaloneCmdId,
                offset,
                count,
                groupOrdinal,
                passes[0],
                passes[0],
                creationOrdinal,
                0,
                0,
                0,
                0));
            creationOrdinal++;
        }
        for (int alias = 0; alias < aliasAcquires.Length; alias++)
        {
            PlannedAliasingBarrier edge = aliasAcquires[alias];
            graph.CommandUnits.Add(new RuntimeCmd(
                queue,
                RuntimeCmd.AliasingBarrierCmdId,
                0,
                0,
                alias,
                edge.StartPasses[0],
                edge.AfterResource,
                creationOrdinal,
                0,
                0,
                0,
                0));
            creationOrdinal++;
        }
        ArenaSlice<int> barrierKeyToCommandUnit =
            graph.AllocateSlice<int>(commandUnitBarriers.KeyCount, clear: false);
        barrierKeyToCommandUnit.Span.Fill(-1);
        for (int barrierKey = 0; barrierKey < commandUnitBarriers.KeyCount; barrierKey++)
        {
            if (commandUnitBarriers.GetCount(barrierKey) == 0) continue;
            int sortPass = barrierKey == graph.Passes.Length ? int.MaxValue : barrierKey;
            barrierKeyToCommandUnit[barrierKey] = graph.CommandUnits.Count;
            graph.CommandUnits.Add(new RuntimeCmd(
                queue,
                RuntimeCmd.BarrierCmdId,
                0,
                0,
                barrierKey,
                sortPass,
                sortPass,
                creationOrdinal,
                0,
                0,
                0,
                0));
            creationOrdinal++;
        }
        ArenaSlice<int> orderedUnits = graph.AllocateSlice<int>(unitCount, clear: false);
        ArenaSlice<int> unitOrder = graph.AllocateSlice<int>(unitCount, clear: false);
        for (int unit = 0; unit < unitCount; unit++) orderedUnits[unit] = unit;
        orderedUnits.Span.Sort((left, right) =>
            CompareOrderedUnit(graph.CommandUnits[left], graph.CommandUnits[right]));
        for (int order = 0; order < unitCount; order++) unitOrder[orderedUnits[order]] = order;

        passToCommandUnit = graph.AllocateSlice<int>(graph.Passes.Length, clear: false);
        passToCommandUnit.Span.Fill(-1);
        ArenaSlice<int> aliasToCommandUnit = graph.AllocateSlice<int>(aliasAcquires.Length, clear: false);
        for (int order = 0; order < orderedUnits.Length; order++)
        {
            int unitOrdinal = orderedUnits[order];
            RuntimeCmd unit = graph.CommandUnits[unitOrdinal];
            if (unit.CmdId is RuntimeCmd.StandaloneCmdId or RuntimeCmd.RasterScopeCmdId)
            {
                foreach (int pass in logicalPassRows.GetReadOnlySpan(unit.PassOffset, unit.PassCount))
                    passToCommandUnit[pass] = unitOrdinal;
            }
            else if (unit.CmdId == RuntimeCmd.AliasingBarrierCmdId)
            {
                aliasToCommandUnit[unit.PayloadOrdinal] = unitOrdinal;
            }
            else
            {
                barrierKeyToCommandUnit[unit.PayloadOrdinal] = unitOrdinal;
            }
        }

        for (int pass = 0; pass < passToCommandUnit.Length; pass++)
        {
            int unit = passToCommandUnit[pass];
            if (unit < 0) continue;
            foreach (int predecessor in graph.GetPassDependencies(pass))
            {
                int predecessorUnit = passToCommandUnit[predecessor];
                if (predecessorUnit < 0)
                    throw new InvalidOperationException("A live pass depends on a culled pass.");
                if (unitOrder[predecessorUnit] > unitOrder[unit])
                {
                    graph.CommandUnits.Clear();
                    return false;
                }
            }
        }
        for (int alias = 0; alias < aliasAcquires.Length; alias++)
        {
            int unit = aliasToCommandUnit[alias];
            foreach (int predecessor in aliasAcquires[alias].EndPasses)
                if (unitOrder[passToCommandUnit[predecessor]] >= unitOrder[unit])
                {
                    graph.CommandUnits.Clear();
                    return false;
                }
            foreach (int successor in aliasAcquires[alias].StartPasses)
                if (unitOrder[passToCommandUnit[successor]] <= unitOrder[unit])
                {
                    graph.CommandUnits.Clear();
                    return false;
                }
        }
        for (int barrierKey = 0; barrierKey < commandUnitBarriers.KeyCount; barrierKey++)
        {
            int unit = barrierKeyToCommandUnit[barrierKey];
            if (unit < 0) continue;
            foreach (int predecessor in commandUnitBarrierPredecessors.CopyToSlice(graph, barrierKey))
                if (unitOrder[passToCommandUnit[predecessor]] >= unitOrder[unit])
                {
                    graph.CommandUnits.Clear();
                    return false;
            }
            if (barrierKey < graph.Passes.Length &&
                unitOrder[passToCommandUnit[barrierKey]] <= unitOrder[unit])
            {
                graph.CommandUnits.Clear();
                return false;
            }
        }

        int passRowCount = 0;
        int barrierRowCount = 0;
        foreach (RuntimeCmd unit in graph.CommandUnits)
        {
            passRowCount = checked(passRowCount + unit.PassCount);
            if (unit.CmdId == RuntimeCmd.BarrierCmdId)
                barrierRowCount = checked(barrierRowCount + commandUnitBarriers.GetCount(unit.PayloadOrdinal));
        }
        ReserveLinearExecutionRows(graph, unitCount, passRowCount);
        graph.CommandUnitResourceBarriers.EnsureCapacity(
            checked(graph.CommandUnitResourceBarriers.Count + barrierRowCount));
        for (int order = 0; order < orderedUnits.Length; order++)
        {
            int unitOrdinal = orderedUnits[order];
            RuntimeCmd unit = graph.CommandUnits[unitOrdinal];
            if (unit.CmdId == RuntimeCmd.BarrierCmdId)
            {
                MaterializeBarrierCommandUnit(
                    graph,
                    unitOrdinal,
                    ref commandUnitBarriers,
                    unit.PayloadOrdinal);
                continue;
            }
            ReadOnlySpan<int> passes = unit.PassCount == 0
                ? []
                : logicalPassRows.GetReadOnlySpan(unit.PassOffset, unit.PassCount);
            PlannedAliasingBarrier? alias = unit.CmdId == RuntimeCmd.AliasingBarrierCmdId
                ? new PlannedAliasingBarrier(
                    aliasAcquires[unit.PayloadOrdinal].BeforeResource,
                    aliasAcquires[unit.PayloadOrdinal].AfterResource,
                    default,
                    default)
                : null;
            MaterializeCommandUnit(graph, unitOrdinal, passes, alias);
        }
        graph.CommandUnitDependencyRows.EnsureCapacity(
            checked(graph.CommandUnitDependencyRows.Count + Math.Max(0, unitCount - 1)));
        for (int order = 0; order < unitCount; order++)
        {
            int unit = orderedUnits[order];
            int offset = graph.CommandUnitDependencyRows.Count;
            if (order != 0) graph.CommandUnitDependencyRows.Add(orderedUnits[order - 1]);
            graph.CommandUnits[unit] = graph.CommandUnits[unit] with
            {
                DependencyOffset = offset,
                DependencyCount = order == 0 ? 0 : 1,
            };
        }
        AppendOrderedCommandBatch(graph, queue, orderedUnits.ReadOnlySpan);
        return true;
    }

    private static int CompareOrderedUnit(in RuntimeCmd left, in RuntimeCmd right)
    {
        int order = left.SortPass.CompareTo(right.SortPass);
        if (order != 0) return order;
        order = OrderedCmdId(left.CmdId).CompareTo(OrderedCmdId(right.CmdId));
        if (order != 0) return order;
        order = left.StableOrdinal.CompareTo(right.StableOrdinal);
        return order != 0 ? order : left.CreationOrdinal.CompareTo(right.CreationOrdinal);
    }

    private static int OrderedCmdId(int cmdId) => cmdId switch
    {
        RuntimeCmd.AliasingBarrierCmdId => 0,
        RuntimeCmd.BarrierCmdId => 1,
        _ => 2,
    };

    private static void BuildCommandBatches(
        RenderGraph graph,
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<int> orderedBuildUnits,
        ArenaSlice<ulong> edges)
    {
        int unitCount = units.Count;
        graph.CommandBatches.EnsureCapacity(checked(graph.CommandBatches.Count + unitCount));
        graph.BatchRuntimeCmds.EnsureCapacity(
            checked(graph.BatchRuntimeCmds.Count + unitCount));
        ArenaSlice<int> unitBatches = graph.AllocateSlice<int>(unitCount, clear: false);
        if (!TryBuildSingleComputeIslandBatches(
                graph,
                units,
                orderedBuildUnits,
                edges,
                unitBatches))
        {
            ArenaSlice<byte> establishedInputs =
                graph.AllocateSlice<byte>(unitCount);
            ArenaSlice<byte> seenImportedResources = graph.HasImportReadiness
                ? graph.AllocateSlice<byte>(checked(graph.ResourceCount * 3))
                : default;
            for (int orderedUnit = 0; orderedUnit < unitCount; orderedUnit++)
            {
                int unitOrdinal = orderedBuildUnits[orderedUnit];
                RuntimeCmd unit = units[unitOrdinal];
                bool beginBatch = graph.CommandBatches.Count == 0;
                if (!beginBatch)
                {
                    CommandBatch current =
                        graph.CommandBatches[graph.CommandBatches.Count - 1];
                    int previousUnit = orderedBuildUnits[orderedUnit - 1];
                    bool newCrossQueueInput = IntroducesCrossQueueInput(
                        units,
                        edges,
                        unitCount,
                        unitOrdinal,
                        establishedInputs);
                    bool previousHasCrossQueueOutput = ExposesCrossQueueOutput(
                        units,
                        edges,
                        unitCount,
                        previousUnit);
                    bool newExternalReadiness =
                        graph.HasImportReadiness &&
                        UnitIntroducesReadiness(
                            graph,
                            unit,
                            seenImportedResources);
                    beginBatch = current.Queue != unit.Queue ||
                        newCrossQueueInput ||
                        previousHasCrossQueueOutput ||
                        newExternalReadiness;
                }

                if (beginBatch)
                {
                    establishedInputs.Span.Clear();
                    graph.CommandBatches.Add(new CommandBatch(
                        unit.Queue,
                        0,
                        0,
                        graph.BatchRuntimeCmds.Count,
                        0,
                        0,
                        0,
                        0,
                        0));
                }
                int batchOrdinal = graph.CommandBatches.Count - 1;
                graph.BatchRuntimeCmds.Add(unitOrdinal);
                ref CommandBatch batch = ref graph.CommandBatches[batchOrdinal];
                batch = batch with
                {
                    CommandUnitCount = batch.CommandUnitCount + 1,
                };
                unitBatches[unitOrdinal] = batchOrdinal;
                AddCrossQueueInputs(
                    units,
                    edges,
                    unitCount,
                    unitOrdinal,
                    establishedInputs);
                if (graph.HasImportReadiness)
                {
                    MarkUnitImportedResources(
                        graph,
                        unit,
                        seenImportedResources);
                }
            }
        }

        BuildBatchDependencies(graph, unitBatches);
    }

    private static unsafe void BuildCommandBatchesSparse(
        RenderGraph graph,
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<int> orderedBuildUnits,
        ArenaSlice<int> incomingOffsets,
        ArenaSlice<int> incomingRows,
        ArenaSlice<int> successorOffsets,
        ArenaSlice<int> successorRows)
    {
        int unitCount = units.Count;
        RuntimeCmd* unitRows = units.DangerousContiguousPointer;
        if (unitCount != 0 && unitRows is null)
            throw new InvalidOperationException(
                "Command-unit rows must be materialized in one reserved arena chunk.");
        graph.CommandBatches.EnsureCapacity(checked(graph.CommandBatches.Count + unitCount));
        graph.BatchRuntimeCmds.EnsureCapacity(
            checked(graph.BatchRuntimeCmds.Count + unitCount));
        ArenaSlice<int> unitBatches = graph.AllocateSlice<int>(unitCount, clear: false);
        if (!TryBuildSingleComputeIslandBatchesSparse(
                graph,
                units,
                orderedBuildUnits,
                incomingOffsets,
                incomingRows,
                successorOffsets,
                successorRows,
                unitBatches))
        {
            ArenaSlice<byte> establishedInputs =
                graph.AllocateSlice<byte>(unitCount);
            ArenaSlice<byte> seenImportedResources = graph.HasImportReadiness
                ? graph.AllocateSlice<byte>(checked(graph.ResourceCount * 3))
                : default;
            int* orderedUnitRows = orderedBuildUnits.DangerousPointer;
            int* unitBatchRows = unitBatches.DangerousPointer;
            for (int orderedUnit = 0; orderedUnit < unitCount; orderedUnit++)
            {
                int unitOrdinal = orderedUnitRows[orderedUnit];
                RuntimeCmd unit = unitRows[unitOrdinal];
                bool beginBatch = graph.CommandBatches.Count == 0;
                if (!beginBatch)
                {
                    CommandBatch current =
                        graph.CommandBatches[graph.CommandBatches.Count - 1];
                    int previousUnit = orderedUnitRows[orderedUnit - 1];
                    bool newCrossQueueInput = IntroducesCrossQueueInputSparse(
                        units,
                        incomingOffsets,
                        incomingRows,
                        unitOrdinal,
                        establishedInputs);
                    bool previousHasCrossQueueOutput = ExposesCrossQueueOutputSparse(
                        units,
                        successorOffsets,
                        successorRows,
                        previousUnit);
                    bool newExternalReadiness =
                        graph.HasImportReadiness &&
                        UnitIntroducesReadiness(
                            graph,
                            unit,
                            seenImportedResources);
                    beginBatch = current.Queue != unit.Queue ||
                        newCrossQueueInput ||
                        previousHasCrossQueueOutput ||
                        newExternalReadiness;
                }

                if (beginBatch)
                {
                    establishedInputs.Span.Clear();
                    graph.CommandBatches.Add(new CommandBatch(
                        unit.Queue,
                        0,
                        0,
                        graph.BatchRuntimeCmds.Count,
                        0,
                        0,
                        0,
                        0,
                        0));
                }
                int batchOrdinal = graph.CommandBatches.Count - 1;
                graph.BatchRuntimeCmds.Add(unitOrdinal);
                ref CommandBatch batch = ref graph.CommandBatches[batchOrdinal];
                batch = batch with
                {
                    CommandUnitCount = batch.CommandUnitCount + 1,
                };
                unitBatchRows[unitOrdinal] = batchOrdinal;
                AddCrossQueueInputsSparse(
                    units,
                    incomingOffsets,
                    incomingRows,
                    unitOrdinal,
                    establishedInputs);
                if (graph.HasImportReadiness)
                {
                    MarkUnitImportedResources(
                        graph,
                        unit,
                        seenImportedResources);
                }
            }
        }

        BuildBatchDependencies(graph, unitBatches);
    }

    private static unsafe void BuildBatchDependencies(
        RenderGraph graph,
        ArenaSlice<int> unitBatches)
    {
        int batchCount = graph.CommandBatches.Count;
        ArenaSlice<int> dependencyMarks = graph.AllocateSlice<int>(batchCount);
        int* dependencyMarkRows = dependencyMarks.DangerousPointer;
        int* unitBatchRows = unitBatches.DangerousPointer;
        CommandBatch* batchRows = graph.CommandBatches.DangerousContiguousPointer;
        RuntimeCmd* unitRows = graph.CommandUnits.DangerousContiguousPointer;
        int* batchUnitRows = graph.BatchRuntimeCmds.DangerousContiguousPointer;
        int* unitDependencyRows =
            graph.CommandUnitDependencyRows.DangerousContiguousPointer;
        if (batchCount != 0 &&
            (batchRows is null ||
             unitRows is null ||
             batchUnitRows is null ||
             (graph.CommandUnitDependencyRows.Count != 0 &&
              unitDependencyRows is null)))
        {
            throw new InvalidOperationException(
                "Execution rows must be materialized in their reserved arena chunks.");
        }
        for (int batchOrdinal = 0; batchOrdinal < batchCount; batchOrdinal++)
        {
            CommandBatch batch = batchRows[batchOrdinal];
            int stamp = batchOrdinal + 1;
            int batchUnitEnd = checked(
                batch.CommandUnitOffset + batch.CommandUnitCount);
            for (int batchUnit = batch.CommandUnitOffset;
                 batchUnit < batchUnitEnd;
                 batchUnit++)
            {
                RuntimeCmd unit = unitRows[batchUnitRows[batchUnit]];
                int dependencyEnd = checked(
                    unit.DependencyOffset + unit.DependencyCount);
                for (int dependency = unit.DependencyOffset;
                     dependency < dependencyEnd;
                     dependency++)
                {
                    int predecessorBatch =
                        unitBatchRows[unitDependencyRows[dependency]];
                    if (predecessorBatch != batchOrdinal)
                        dependencyMarkRows[predecessorBatch] = stamp;
                }
            }
            int dependencyOffset = graph.BatchDependencyRows.Count;
            int dependencyCount = 0;
            for (int predecessorBatch = 0; predecessorBatch < batchOrdinal; predecessorBatch++)
                if (dependencyMarkRows[predecessorBatch] == stamp) dependencyCount++;
            graph.BatchDependencyRows.EnsureAppendCapacity(dependencyCount);
            for (int predecessorBatch = 0; predecessorBatch < batchOrdinal; predecessorBatch++)
                if (dependencyMarkRows[predecessorBatch] == stamp)
                    graph.BatchDependencyRows.Add(predecessorBatch);
            batchRows[batchOrdinal] = batch with
            {
                DependencyOffset = dependencyOffset,
                DependencyCount = graph.BatchDependencyRows.Count - dependencyOffset,
            };
        }
    }

    private static unsafe void BuildSparseUnitOrdering(
        RenderGraph graph,
        int logicalPassGroupCount,
        ArenaSlice<PlannedAliasingBarrier> aliasAcquires,
        int aliasBase,
        PassBarrierTable commandUnitBarriers,
        PassPredecessorTable commandUnitBarrierPredecessors,
        ArenaSlice<int> barrierKeyToBuildUnit,
        ArenaSlice<int> passToBuildUnit,
        int unitCount,
        out ArenaSlice<int> orderedBuildUnits,
        out ArenaSlice<int> unitOrder,
        out ArenaSlice<int> incomingOffsets,
        out ArenaSlice<int> incomingRows,
        out ArenaSlice<int> successorOffsets,
        out ArenaSlice<int> successorRows,
        out int edgeCount)
    {
        int edgeCapacity = checked(
            graph.DependencyRows.Count +
            logicalPassGroupCount +
            commandUnitBarrierPredecessors.EntryCount +
            commandUnitBarriers.NonEmptyKeyCount);
        foreach (PlannedAliasingBarrier alias in aliasAcquires)
        {
            edgeCapacity = checked(
                edgeCapacity +
                alias.EndPasses.Length +
                alias.StartPasses.Length);
        }

        ArenaSlice<int> incomingHeads =
            graph.AllocateSlice<int>(unitCount, clear: false);
        incomingHeads.Span.Fill(-1);
        ArenaSlice<int> edgeNext =
            graph.AllocateSlice<int>(edgeCapacity, clear: false);
        ArenaSlice<int> edgePredecessors =
            graph.AllocateSlice<int>(edgeCapacity, clear: false);
        ArenaSlice<int> edgeSuccessors =
            graph.AllocateSlice<int>(edgeCapacity, clear: false);
        ArenaSlice<int> indegrees = graph.AllocateSlice<int>(unitCount);
        int* incomingHeadRows = incomingHeads.DangerousPointer;
        int* edgeNextRows = edgeNext.DangerousPointer;
        int* edgePredecessorRows = edgePredecessors.DangerousPointer;
        int* edgeSuccessorRows = edgeSuccessors.DangerousPointer;
        int* indegreeRows = indegrees.DangerousPointer;
        int* passBuildUnitRows = passToBuildUnit.DangerousPointer;
        int* barrierBuildUnitRows = barrierKeyToBuildUnit.DangerousPointer;
        ReadOnlySpan<RuntimeCmd> unitRows =
            graph.CommandUnits.GetReadOnlySpan(0, unitCount);
        edgeCount = 0;

        for (int pass = 0; pass < passToBuildUnit.Length; pass++)
        {
            int unit = passBuildUnitRows[pass];
            if (unit < 0) continue;
            foreach (int predecessor in graph.GetPassDependencies(pass))
            {
                int predecessorUnit = passBuildUnitRows[predecessor];
                if (predecessorUnit < 0)
                    throw new InvalidOperationException("A live pass depends on a culled pass.");
                AddSparseUnitDependency(
                    incomingHeadRows,
                    edgeNextRows,
                    edgePredecessorRows,
                    edgeSuccessorRows,
                    indegreeRows,
                    edgeCapacity,
                    ref edgeCount,
                    predecessorUnit,
                    unit);
            }
        }

        ArenaSlice<int> previousOnQueue =
            graph.AllocateSlice<int>(3, clear: false);
        previousOnQueue.Span.Fill(-1);
        for (int groupOrdinal = 0; groupOrdinal < logicalPassGroupCount; groupOrdinal++)
        {
            RuntimeCmd unit = unitRows[groupOrdinal];
            int queue = (int)unit.Queue;
            if (previousOnQueue[queue] >= 0)
            {
                AddSparseUnitDependency(
                    incomingHeadRows,
                    edgeNextRows,
                    edgePredecessorRows,
                    edgeSuccessorRows,
                    indegreeRows,
                    edgeCapacity,
                    ref edgeCount,
                    previousOnQueue[queue],
                    groupOrdinal);
            }
            previousOnQueue[queue] = groupOrdinal;
        }

        for (int alias = 0; alias < aliasAcquires.Length; alias++)
        {
            int unit = checked(aliasBase + alias);
            PlannedAliasingBarrier edge = aliasAcquires[alias];
            foreach (int predecessor in edge.EndPasses)
            {
                AddSparseUnitDependency(
                    incomingHeadRows,
                    edgeNextRows,
                    edgePredecessorRows,
                    edgeSuccessorRows,
                    indegreeRows,
                    edgeCapacity,
                    ref edgeCount,
                    passBuildUnitRows[predecessor],
                    unit);
            }
            foreach (int successor in edge.StartPasses)
            {
                AddSparseUnitDependency(
                    incomingHeadRows,
                    edgeNextRows,
                    edgePredecessorRows,
                    edgeSuccessorRows,
                    indegreeRows,
                    edgeCapacity,
                    ref edgeCount,
                    unit,
                    passBuildUnitRows[successor]);
            }
        }

        for (int barrierKey = 0;
             barrierKey < commandUnitBarriers.KeyCount;
             barrierKey++)
        {
            int unit = barrierBuildUnitRows[barrierKey];
            if (unit < 0) continue;
            ReadOnlySpan<ulong> predecessorWords =
                commandUnitBarrierPredecessors.GetWords(barrierKey);
            for (int wordIndex = 0;
                 wordIndex < predecessorWords.Length;
                 wordIndex++)
            {
                ulong word = predecessorWords[wordIndex];
                while (word != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                    int predecessor = checked((wordIndex << 6) + bit);
                    AddSparseUnitDependency(
                        incomingHeadRows,
                        edgeNextRows,
                        edgePredecessorRows,
                        edgeSuccessorRows,
                        indegreeRows,
                        edgeCapacity,
                        ref edgeCount,
                        passBuildUnitRows[predecessor],
                        unit);
                    word &= word - 1;
                }
            }
            if (barrierKey < graph.Passes.Length)
            {
                AddSparseUnitDependency(
                    incomingHeadRows,
                    edgeNextRows,
                    edgePredecessorRows,
                    edgeSuccessorRows,
                    indegreeRows,
                    edgeCapacity,
                    ref edgeCount,
                    unit,
                    passBuildUnitRows[barrierKey]);
            }
        }

        incomingOffsets = graph.AllocateSlice<int>(checked(unitCount + 1));
        successorOffsets = graph.AllocateSlice<int>(checked(unitCount + 1));
        int* incomingOffsetRows = incomingOffsets.DangerousPointer;
        int* successorOffsetRows = successorOffsets.DangerousPointer;
        for (int edge = 0; edge < edgeCount; edge++)
        {
            incomingOffsetRows[edgeSuccessorRows[edge] + 1]++;
            successorOffsetRows[edgePredecessorRows[edge] + 1]++;
        }
        for (int unit = 0; unit < unitCount; unit++)
        {
            incomingOffsetRows[unit + 1] += incomingOffsetRows[unit];
            successorOffsetRows[unit + 1] += successorOffsetRows[unit];
        }

        incomingRows = graph.AllocateSlice<int>(edgeCount, clear: false);
        successorRows = graph.AllocateSlice<int>(edgeCount, clear: false);
        ArenaSlice<int> incomingCursors =
            graph.AllocateSlice<int>(unitCount, clear: false);
        ArenaSlice<int> successorCursors =
            graph.AllocateSlice<int>(unitCount, clear: false);
        incomingOffsets.ReadOnlySpan[..unitCount].CopyTo(incomingCursors.Span);
        successorOffsets.ReadOnlySpan[..unitCount].CopyTo(successorCursors.Span);
        int* incomingRowValues = incomingRows.DangerousPointer;
        int* successorRowValues = successorRows.DangerousPointer;
        int* incomingCursorRows = incomingCursors.DangerousPointer;
        int* successorCursorRows = successorCursors.DangerousPointer;
        for (int edge = 0; edge < edgeCount; edge++)
        {
            int predecessor = edgePredecessorRows[edge];
            int successor = edgeSuccessorRows[edge];
            incomingRowValues[incomingCursorRows[successor]++] = predecessor;
            successorRowValues[successorCursorRows[predecessor]++] = successor;
        }

        orderedBuildUnits =
            graph.AllocateSlice<int>(unitCount, clear: false);
        unitOrder = graph.AllocateSlice<int>(unitCount, clear: false);
        ArenaSlice<byte> emitted = graph.AllocateSlice<byte>(unitCount);
        int* orderedUnitRows = orderedBuildUnits.DangerousPointer;
        int* unitOrderRows = unitOrder.DangerousPointer;
        byte* emittedRows = emitted.DangerousPointer;
        for (int ordinal = 0; ordinal < unitCount; ordinal++)
        {
            int selected = -1;
            for (int candidate = 0; candidate < unitCount; candidate++)
            {
                if (emittedRows[candidate] != 0 || indegreeRows[candidate] != 0)
                    continue;
                if (selected < 0 ||
                    CompareUnit(
                        unitRows[candidate],
                        unitRows[selected]) < 0)
                {
                    selected = candidate;
                }
            }
            if (selected < 0)
                throw new InvalidOperationException(
                    "The command-unit dependency graph contains a cycle.");
            emittedRows[selected] = 1;
            orderedUnitRows[ordinal] = selected;
            unitOrderRows[selected] = ordinal;
            for (int edge = successorOffsetRows[selected];
                 edge < successorOffsetRows[selected + 1];
                 edge++)
            {
                indegreeRows[successorRowValues[edge]]--;
            }
        }
    }

    private static unsafe void AddSparseUnitDependency(
        int* incomingHeads,
        int* edgeNext,
        int* edgePredecessors,
        int* edgeSuccessors,
        int* indegrees,
        int edgeCapacity,
        ref int edgeCount,
        int predecessor,
        int successor)
    {
        if (predecessor < 0 || successor < 0)
            throw new InvalidOperationException(
                "A command-unit dependency references a missing logical pass.");
        if (predecessor == successor) return;
        for (int edge = incomingHeads[successor];
             edge >= 0;
             edge = edgeNext[edge])
        {
            if (edgePredecessors[edge] == predecessor) return;
        }
        if ((uint)edgeCount >= (uint)edgeCapacity)
            throw new InvalidOperationException(
                "The command-unit dependency capacity was undercounted.");
        int appended = edgeCount++;
        edgePredecessors[appended] = predecessor;
        edgeSuccessors[appended] = successor;
        edgeNext[appended] = incomingHeads[successor];
        incomingHeads[successor] = appended;
        indegrees[successor]++;
    }

    private static unsafe void SortDependenciesByUnitOrder(
        Span<int> dependencies,
        ArenaSlice<int> unitOrder)
    {
        int* unitOrderRows = unitOrder.DangerousPointer;
        for (int index = 1; index < dependencies.Length; index++)
        {
            int value = dependencies[index];
            int valueOrder = unitOrderRows[value];
            int insertion = index;
            while (insertion > 0 &&
                   unitOrderRows[dependencies[insertion - 1]] > valueOrder)
            {
                dependencies[insertion] = dependencies[insertion - 1];
                insertion--;
            }
            dependencies[insertion] = value;
        }
    }

    private static void AddUnitDependency(
        ArenaSlice<ulong> edges,
        ArenaSlice<int> indegrees,
        int unitCount,
        int predecessor,
        int successor)
    {
        if (predecessor < 0 || successor < 0)
            throw new InvalidOperationException("A command-unit dependency references a missing logical pass.");
        if (predecessor == successor) return;
        int bit = checked(predecessor * unitCount + successor);
        int word = bit >> 6;
        ulong mask = 1UL << (bit & 63);
        if ((edges[word] & mask) != 0) return;
        edges[word] |= mask;
        indegrees[successor]++;
    }

    private static bool HasUnitDependency(
        ArenaSlice<ulong> edges,
        int unitCount,
        int predecessor,
        int successor)
    {
        int bit = checked(predecessor * unitCount + successor);
        return (edges[bit >> 6] & (1UL << (bit & 63))) != 0;
    }

    private static int CompareUnit(in RuntimeCmd left, in RuntimeCmd right)
    {
        int order = left.SortPass.CompareTo(right.SortPass);
        if (order != 0) return order;
        int leftKind = left.CmdId == RuntimeCmd.AliasingBarrierCmdId ? 0 : 1;
        int rightKind = right.CmdId == RuntimeCmd.AliasingBarrierCmdId ? 0 : 1;
        order = leftKind.CompareTo(rightKind);
        if (order != 0) return order;
        order = left.StableOrdinal.CompareTo(right.StableOrdinal);
        return order != 0 ? order : left.CreationOrdinal.CompareTo(right.CreationOrdinal);
    }

    private static bool IntroducesCrossQueueInput(
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<ulong> edges,
        int unitCount,
        int unit,
        ArenaSlice<byte> established)
    {
        for (int predecessor = 0; predecessor < unitCount; predecessor++)
            if (HasUnitDependency(edges, unitCount, predecessor, unit) &&
                units[predecessor].Queue != units[unit].Queue &&
                established[predecessor] == 0)
                return true;
        return false;
    }

    private static void AddCrossQueueInputs(
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<ulong> edges,
        int unitCount,
        int unit,
        ArenaSlice<byte> established)
    {
        for (int predecessor = 0; predecessor < unitCount; predecessor++)
            if (HasUnitDependency(edges, unitCount, predecessor, unit) &&
                units[predecessor].Queue != units[unit].Queue)
                established[predecessor] = 1;
    }

    private static bool ExposesCrossQueueOutput(
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<ulong> edges,
        int unitCount,
        int unit)
    {
        for (int successor = 0; successor < unitCount; successor++)
            if (HasUnitDependency(edges, unitCount, unit, successor) &&
                units[successor].Queue != units[unit].Queue)
                return true;
        return false;
    }

    private static bool IntroducesCrossQueueInputSparse(
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<int> incomingOffsets,
        ArenaSlice<int> incomingRows,
        int unit,
        ArenaSlice<byte> established)
    {
        for (int edge = incomingOffsets[unit];
             edge < incomingOffsets[unit + 1];
             edge++)
        {
            int predecessor = incomingRows[edge];
            if (units[predecessor].Queue != units[unit].Queue &&
                established[predecessor] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void AddCrossQueueInputsSparse(
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<int> incomingOffsets,
        ArenaSlice<int> incomingRows,
        int unit,
        ArenaSlice<byte> established)
    {
        for (int edge = incomingOffsets[unit];
             edge < incomingOffsets[unit + 1];
             edge++)
        {
            int predecessor = incomingRows[edge];
            if (units[predecessor].Queue != units[unit].Queue)
                established[predecessor] = 1;
        }
    }

    private static bool ExposesCrossQueueOutputSparse(
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<int> successorOffsets,
        ArenaSlice<int> successorRows,
        int unit)
    {
        for (int edge = successorOffsets[unit];
             edge < successorOffsets[unit + 1];
             edge++)
        {
            if (units[successorRows[edge]].Queue != units[unit].Queue)
                return true;
        }
        return false;
    }

    private static bool UnitIntroducesReadiness(
        RenderGraph graph,
        in RuntimeCmd unit,
        ArenaSlice<byte> seen)
    {
        foreach (int pass in graph.GetCommandUnitPasses(unit))
        foreach (ref readonly PassInputData access in graph.GetPassAccesses(graph.Passes[pass]))
        {
            int resource = graph.GetResourceOrdinal(access);
            if (!graph.IsResourceImported(resource)) continue;
            int seenIndex = checked(resource * 3 + (int)unit.Queue);
            if (seen[seenIndex] != 0) continue;
            foreach (QueueCompletion completion in graph.GetResourceReadiness(resource))
                if (completion.Queue.Type != unit.Queue) return true;
        }
        foreach (PlannedBarrier barrier in graph.GetCommandUnitBarriers(unit))
        {
            int resource = barrier.Resource;
            if (!graph.IsResourceImported(resource)) continue;
            int seenIndex = checked(resource * 3 + (int)unit.Queue);
            if (seen[seenIndex] != 0) continue;
            foreach (QueueCompletion completion in graph.GetResourceReadiness(resource))
                if (completion.Queue.Type != unit.Queue) return true;
        }
        return false;
    }

    private static void MarkUnitImportedResources(
        RenderGraph graph,
        in RuntimeCmd unit,
        ArenaSlice<byte> seen)
    {
        foreach (int pass in graph.GetCommandUnitPasses(unit))
        foreach (ref readonly PassInputData access in graph.GetPassAccesses(graph.Passes[pass]))
        {
            int resource = graph.GetResourceOrdinal(access);
            if (graph.IsResourceImported(resource))
                seen[checked(resource * 3 + (int)unit.Queue)] = 1;
        }
        foreach (PlannedBarrier barrier in graph.GetCommandUnitBarriers(unit))
            if (graph.IsResourceImported(barrier.Resource))
                seen[checked(barrier.Resource * 3 + (int)unit.Queue)] = 1;
    }

    private static bool TryBuildLinearExecution(
        RenderGraph graph,
        ArenaSlice<QueueType> queues,
        ArenaColumn<int> logicalPassRows,
        ArenaColumn<int> logicalPassStarts,
        ArenaSlice<PlannedAliasingBarrier> aliasAcquires,
        PassBarrierTable commandUnitBarriers,
        out ArenaSlice<int> passToCommandUnit)
    {
        passToCommandUnit = default;
        if (aliasAcquires.Length != 0 || commandUnitBarriers.NonEmptyKeyCount != 0) return false;

        passToCommandUnit = graph.AllocateSlice<int>(graph.Passes.Length, clear: false);
        passToCommandUnit.Span.Fill(-1);
        int logicalPassGroupCount = logicalPassStarts.Count - 1;
        if (logicalPassGroupCount == 0) return true;

        ReadOnlySpan<int> firstGroup =
            GetPasses(logicalPassRows, logicalPassStarts, 0);
        if (firstGroup.Length == 0) throw new InvalidOperationException("A logical record group cannot be empty.");
        QueueType queue = queues[firstGroup[0]];
        ArenaSlice<byte> seenImportedResources = graph.HasImportReadiness
            ? graph.AllocateSlice<byte>(graph.ResourceCount)
            : default;
        for (int unit = 0; unit < logicalPassGroupCount; unit++)
        {
            ReadOnlySpan<int> group =
                GetPasses(logicalPassRows, logicalPassStarts, unit);
            if (group.Length == 0) throw new InvalidOperationException("A logical record group cannot be empty.");
            if (queues[group[0]] != queue) return false;
            foreach (int pass in group)
            {
                if (queues[pass] != queue || passToCommandUnit[pass] >= 0)
                    throw new InvalidOperationException("Logical record groups must uniquely contain same-queue passes.");
                if (graph.HasImportReadiness && unit != 0 &&
                    IntroducesCrossQueueReadiness(graph, pass, queue, seenImportedResources))
                    return false;
                passToCommandUnit[pass] = unit;
            }
            if (graph.HasImportReadiness)
                foreach (int pass in group) MarkImportedResources(graph, pass, seenImportedResources);
        }

        for (int unit = 0; unit < logicalPassGroupCount; unit++)
        foreach (int pass in GetPasses(logicalPassRows, logicalPassStarts, unit))
        foreach (int predecessor in graph.GetPassDependencies(pass))
        {
            int predecessorUnit = passToCommandUnit[predecessor];
            if (predecessorUnit < 0)
                throw new InvalidOperationException("A live pass depends on a culled pass.");
            if (predecessorUnit > unit) return false;
        }

        ReserveLinearExecutionRows(graph, logicalPassGroupCount, logicalPassRows.Count);
        for (int unit = 0; unit < logicalPassGroupCount; unit++)
        {
            ReadOnlySpan<int> passes =
                GetPasses(logicalPassRows, logicalPassStarts, unit);
            AppendCommandUnit(
                graph,
                queue,
                passes.Length > 1 ? RuntimeCmd.RasterScopeCmdId : RuntimeCmd.StandaloneCmdId,
                passes,
                null);
        }
        AppendLinearCommandBatch(graph, queue, logicalPassGroupCount);
        return true;
    }

    private static void ReserveLinearExecutionRows(
        RenderGraph graph,
        int unitCount,
        int passCount)
    {
        graph.CommandUnits.EnsureCapacity(checked(graph.CommandUnits.Count + unitCount));
        graph.CommandUnitPassRows.EnsureCapacity(checked(graph.CommandUnitPassRows.Count + passCount));
        graph.BatchRuntimeCmds.EnsureCapacity(
            checked(graph.BatchRuntimeCmds.Count + unitCount));
        graph.CommandBatches.EnsureCapacity(checked(graph.CommandBatches.Count + 1));
    }

    private static void AppendCommandUnit(
        RenderGraph graph,
        QueueType queue,
        int cmdId,
        ReadOnlySpan<int> passes,
        PlannedAliasingBarrier? alias)
    {
        int passOffset = graph.CommandUnitPassRows.Count;
        foreach (int pass in passes) graph.CommandUnitPassRows.Add(pass);
        int aliasOffset = graph.CommandUnitAliasRows.Count;
        if (alias is PlannedAliasingBarrier value) graph.CommandUnitAliasRows.Add(value);
        int barrierOffset = graph.CommandUnitResourceBarriers.Count;
        graph.CommandUnits.Add(new RuntimeCmd(
            queue,
            cmdId,
            passOffset,
            passes.Length,
            -1,
            passes.IsEmpty ? 0 : passes[0],
            passes.IsEmpty ? 0 : passes[0],
            graph.CommandUnits.Count,
            aliasOffset,
            alias is null ? 0 : 1,
            barrierOffset,
            0));
    }

    private static void MaterializeCommandUnit(
        RenderGraph graph,
        int unitOrdinal,
        ReadOnlySpan<int> passes,
        PlannedAliasingBarrier? alias)
    {
        int passOffset = graph.CommandUnitPassRows.Count;
        foreach (int pass in passes) graph.CommandUnitPassRows.Add(pass);
        int aliasOffset = graph.CommandUnitAliasRows.Count;
        if (alias is PlannedAliasingBarrier value) graph.CommandUnitAliasRows.Add(value);
        int barrierOffset = graph.CommandUnitResourceBarriers.Count;
        graph.CommandUnits[unitOrdinal] = graph.CommandUnits[unitOrdinal] with
        {
            PassOffset = passOffset,
            PassCount = passes.Length,
            AliasOffset = aliasOffset,
            AliasCount = alias is null ? 0 : 1,
            BarrierOffset = barrierOffset,
            BarrierCount = 0,
            PayloadOrdinal = -1,
        };
    }

    private static void MaterializeBarrierCommandUnit(
        RenderGraph graph,
        int unitOrdinal,
        ref PassBarrierTable barriers,
        int barrierKey)
    {
        barriers.AppendTo(
            ref graph.CommandUnitResourceBarriers,
            barrierKey,
            out int barrierOffset,
            out int barrierCount);
        graph.CommandUnits[unitOrdinal] = graph.CommandUnits[unitOrdinal] with
        {
            PassOffset = graph.CommandUnitPassRows.Count,
            PassCount = 0,
            AliasOffset = graph.CommandUnitAliasRows.Count,
            AliasCount = 0,
            BarrierOffset = barrierOffset,
            BarrierCount = barrierCount,
            PayloadOrdinal = -1,
        };
    }

    private static void AppendLinearCommandBatch(
        RenderGraph graph,
        QueueType queue,
        int unitCount)
    {
        int unitOffset = graph.BatchRuntimeCmds.Count;
        for (int unit = 0; unit < unitCount; unit++) graph.BatchRuntimeCmds.Add(unit);
        graph.CommandBatches.Add(new CommandBatch(
            queue,
            graph.BatchDependencyRows.Count,
            0,
            unitOffset,
            unitCount,
            0,
            0,
            0,
            0));
    }

    private static void AppendOrderedCommandBatch(
        RenderGraph graph,
        QueueType queue,
        ReadOnlySpan<int> orderedUnits)
    {
        int unitOffset = graph.BatchRuntimeCmds.Count;
        foreach (int unit in orderedUnits) graph.BatchRuntimeCmds.Add(unit);
        graph.CommandBatches.Add(new CommandBatch(
            queue,
            graph.BatchDependencyRows.Count,
            0,
            unitOffset,
            orderedUnits.Length,
            0,
            0,
            0,
            0));
    }

    private static unsafe void BuildBatchResourcesAndExternalWaits(RenderGraph graph)
    {
        if (graph.CommandBatches.Count == 0) return;
        int batchCount = graph.CommandBatches.Count;
        int resourceCount = graph.ResourceCount;
        int bufferCount = graph.Buffers.Count;
        graph.BatchResourceRows.EnsureCapacity(checked(
            graph.BatchResourceRows.Count +
            checked(batchCount * resourceCount)));
        if (graph.HasImportReadiness)
        {
            graph.BatchExternalWaitRows.EnsureCapacity(checked(
                graph.BatchExternalWaitRows.Count +
                checked(batchCount * 3)));
        }
        ArenaSlice<int> resourceMarks = graph.AllocateSlice<int>(resourceCount);
        int* resourceMarkRows = resourceMarks.DangerousPointer;
        ArenaSlice<byte> seenOnQueue = graph.HasImportReadiness
            ? graph.AllocateSlice<byte>(checked(resourceCount * 3))
            : default;
        byte* seenOnQueueRows = seenOnQueue.DangerousPointer;
        CommandBatch* batchRows =
            graph.CommandBatches.DangerousContiguousPointer;
        int* batchUnitRows =
            graph.BatchRuntimeCmds.DangerousContiguousPointer;
        RuntimeCmd* unitRows =
            graph.CommandUnits.DangerousContiguousPointer;
        int* commandUnitPassRows =
            graph.CommandUnitPassRows.DangerousContiguousPointer;
        PlannedAliasingBarrier* aliasRows =
            graph.CommandUnitAliasRows.DangerousContiguousPointer;
        PlannedBarrier* barrierRows =
            graph.CommandUnitResourceBarriers.DangerousContiguousPointer;
        PassData* passRows = graph.Passes.DangerousContiguousPointer;
        PassInputData* accessRows = graph.PassInputs.DangerousContiguousPointer;
        QueueCompletion[] externalWaits = new QueueCompletion[3];
        Span<ulong> establishedWaitValues = stackalloc ulong[9];
        for (int batchOrdinal = 0; batchOrdinal < batchCount; batchOrdinal++)
        {
            CommandBatch batch = batchRows is not null
                ? batchRows[batchOrdinal]
                : graph.CommandBatches[batchOrdinal];
            int resourceOffset = graph.BatchResourceRows.Count;
            int waitOffset = graph.BatchExternalWaitRows.Count;
            int stamp = batchOrdinal + 1;
            externalWaits.AsSpan().Clear();
            int batchUnitEnd = checked(
                batch.CommandUnitOffset + batch.CommandUnitCount);
            for (int batchUnit = batch.CommandUnitOffset;
                 batchUnit < batchUnitEnd;
                 batchUnit++)
            {
                int unitOrdinal = batchUnitRows is not null
                    ? batchUnitRows[batchUnit]
                    : graph.BatchRuntimeCmds[batchUnit];
                RuntimeCmd unit = unitRows is not null
                    ? unitRows[unitOrdinal]
                    : graph.CommandUnits[unitOrdinal];
                int unitPassEnd = checked(unit.PassOffset + unit.PassCount);
                for (int unitPass = unit.PassOffset;
                     unitPass < unitPassEnd;
                     unitPass++)
                {
                    int pass = commandUnitPassRows is not null
                        ? commandUnitPassRows[unitPass]
                        : graph.CommandUnitPassRows[unitPass];
                    PassData passRow = passRows is not null
                        ? passRows[pass]
                        : graph.Passes[pass];
                    int accessEnd =
                        checked(passRow.AccessOffset + passRow.AccessCount);
                    for (int accessOrdinal = passRow.AccessOffset;
                         accessOrdinal < accessEnd;
                         accessOrdinal++)
                    {
                        PassInputData access = accessRows is not null
                            ? accessRows[accessOrdinal]
                            : graph.PassInputs[accessOrdinal];
                        int resource = access.IsBuffer
                            ? access.Buffer
                            : checked(bufferCount + access.Texture);
                        bool added = AppendBatchResource(
                            graph,
                            resource,
                            stamp,
                            resourceMarkRows);
                        if (added && graph.HasImportReadiness)
                        {
                            AppendExternalWaits(
                                graph,
                                resource,
                                batch.Queue,
                                seenOnQueueRows,
                                externalWaits,
                                establishedWaitValues);
                        }
                    }
                }
                int aliasEnd = checked(unit.AliasOffset + unit.AliasCount);
                for (int aliasOrdinal = unit.AliasOffset;
                     aliasOrdinal < aliasEnd;
                     aliasOrdinal++)
                {
                    PlannedAliasingBarrier alias = aliasRows is not null
                        ? aliasRows[aliasOrdinal]
                        : graph.CommandUnitAliasRows[aliasOrdinal];
                    AppendBatchResource(
                        graph,
                        alias.BeforeResource,
                        stamp,
                        resourceMarkRows);
                    AppendBatchResource(
                        graph,
                        alias.AfterResource,
                        stamp,
                        resourceMarkRows);
                }
                int barrierEnd =
                    checked(unit.BarrierOffset + unit.BarrierCount);
                for (int barrierOrdinal = unit.BarrierOffset;
                     barrierOrdinal < barrierEnd;
                     barrierOrdinal++)
                {
                    PlannedBarrier barrier = barrierRows is not null
                        ? barrierRows[barrierOrdinal]
                        : graph.CommandUnitResourceBarriers[barrierOrdinal];
                    bool added = AppendBatchResource(
                        graph,
                        barrier.Resource,
                        stamp,
                        resourceMarkRows);
                    if (added && graph.HasImportReadiness)
                        AppendExternalWaits(
                            graph,
                            barrier.Resource,
                            batch.Queue,
                            seenOnQueueRows,
                            externalWaits,
                            establishedWaitValues);
                }
            }
            foreach (QueueCompletion wait in externalWaits)
                if (wait != default)
                    graph.BatchExternalWaitRows.Add(wait);
            CommandBatch updated = batch with
            {
                ResourceOffset = resourceOffset,
                ResourceCount = graph.BatchResourceRows.Count - resourceOffset,
                ExternalWaitOffset = waitOffset,
                ExternalWaitCount = graph.BatchExternalWaitRows.Count - waitOffset,
            };
            if (batchRows is not null)
                batchRows[batchOrdinal] = updated;
            else
                graph.CommandBatches[batchOrdinal] = updated;
        }
    }

    private static bool TryBuildSingleComputeIslandBatches(
        RenderGraph graph,
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<int> orderedBuildUnits,
        ArenaSlice<ulong> edges,
        ArenaSlice<int> unitBatches)
    {
        int unitCount = units.Count;
        bool hasGraphics = false;
        bool hasCompute = false;
        for (int unit = 0; unit < unitCount; unit++)
        {
            switch (units[unit].Queue)
            {
                case QueueType.Graphics:
                    hasGraphics = true;
                    break;
                case QueueType.Compute:
                    hasCompute = true;
                    break;
                default:
                    return false;
            }
        }
        if (!hasGraphics || !hasCompute) return false;

        ArenaSlice<byte> reachesCompute =
            graph.AllocateSlice<byte>(unitCount);
        ArenaSlice<byte> followsCompute =
            graph.AllocateSlice<byte>(unitCount);
        for (int order = orderedBuildUnits.Length - 1; order >= 0; order--)
        {
            int unit = orderedBuildUnits[order];
            if (units[unit].Queue == QueueType.Compute)
            {
                reachesCompute[unit] = 1;
                continue;
            }
            for (int successor = 0; successor < unitCount; successor++)
            {
                if (reachesCompute[successor] == 0 ||
                    !HasUnitDependency(edges, unitCount, unit, successor))
                {
                    continue;
                }
                reachesCompute[unit] = 1;
                break;
            }
        }
        for (int order = 0; order < orderedBuildUnits.Length; order++)
        {
            int unit = orderedBuildUnits[order];
            if (units[unit].Queue == QueueType.Compute)
            {
                followsCompute[unit] = 1;
                continue;
            }
            for (int predecessor = 0; predecessor < unitCount; predecessor++)
            {
                if (followsCompute[predecessor] == 0 ||
                    !HasUnitDependency(edges, unitCount, predecessor, unit))
                {
                    continue;
                }
                followsCompute[unit] = 1;
                break;
            }
        }

        ArenaSlice<byte> classes =
            graph.AllocateSlice<byte>(unitCount, clear: false);
        Span<int> classCounts = stackalloc int[4];
        for (int unit = 0; unit < unitCount; unit++)
        {
            byte unitClass;
            if (units[unit].Queue == QueueType.Compute)
            {
                unitClass = 2;
            }
            else
            {
                bool before = reachesCompute[unit] != 0;
                bool after = followsCompute[unit] != 0;
                if (before && after) return false;
                unitClass = before
                    ? (byte)0
                    : after
                        ? (byte)3
                        : (byte)1;
            }
            classes[unit] = unitClass;
            classCounts[unitClass]++;
        }

        for (int predecessor = 0; predecessor < unitCount; predecessor++)
        for (int successor = 0; successor < unitCount; successor++)
        {
            if (!HasUnitDependency(
                    edges,
                    unitCount,
                    predecessor,
                    successor))
            {
                continue;
            }
            if (classes[predecessor] > classes[successor])
                return false;
        }

        for (byte unitClass = 0; unitClass < 4; unitClass++)
        {
            int count = classCounts[unitClass];
            if (count == 0) continue;
            QueueType queue =
                unitClass == 2 ? QueueType.Compute : QueueType.Graphics;
            int unitOffset = graph.BatchRuntimeCmds.Count;
            int batchOrdinal = graph.CommandBatches.Count;
            foreach (int unit in orderedBuildUnits)
            {
                if (classes[unit] != unitClass) continue;
                graph.BatchRuntimeCmds.Add(unit);
                unitBatches[unit] = batchOrdinal;
            }
            graph.CommandBatches.Add(new CommandBatch(
                queue,
                0,
                0,
                unitOffset,
                count,
                0,
                0,
                0,
                0));
        }
        return true;
    }

    private static unsafe bool TryBuildSingleComputeIslandBatchesSparse(
        RenderGraph graph,
        ArenaColumn<RuntimeCmd> units,
        ArenaSlice<int> orderedBuildUnits,
        ArenaSlice<int> incomingOffsets,
        ArenaSlice<int> incomingRows,
        ArenaSlice<int> successorOffsets,
        ArenaSlice<int> successorRows,
        ArenaSlice<int> unitBatches)
    {
        int unitCount = units.Count;
        RuntimeCmd* unitRows = units.DangerousContiguousPointer;
        int* orderedUnitRows = orderedBuildUnits.DangerousPointer;
        int* incomingOffsetRows = incomingOffsets.DangerousPointer;
        int* incomingRowValues = incomingRows.DangerousPointer;
        int* successorOffsetRows = successorOffsets.DangerousPointer;
        int* successorRowValues = successorRows.DangerousPointer;
        int* unitBatchRows = unitBatches.DangerousPointer;
        if (unitCount != 0 && unitRows is null)
            throw new InvalidOperationException(
                "Command-unit rows must be materialized in one reserved arena chunk.");
        bool hasGraphics = false;
        bool hasCompute = false;
        for (int unit = 0; unit < unitCount; unit++)
        {
            switch (unitRows[unit].Queue)
            {
                case QueueType.Graphics:
                    hasGraphics = true;
                    break;
                case QueueType.Compute:
                    hasCompute = true;
                    break;
                default:
                    return false;
            }
        }
        if (!hasGraphics || !hasCompute) return false;

        ArenaSlice<byte> reachesCompute =
            graph.AllocateSlice<byte>(unitCount);
        ArenaSlice<byte> followsCompute =
            graph.AllocateSlice<byte>(unitCount);
        byte* reachesComputeRows = reachesCompute.DangerousPointer;
        byte* followsComputeRows = followsCompute.DangerousPointer;
        for (int order = orderedBuildUnits.Length - 1; order >= 0; order--)
        {
            int unit = orderedUnitRows[order];
            if (unitRows[unit].Queue == QueueType.Compute)
            {
                reachesComputeRows[unit] = 1;
                continue;
            }
            for (int edge = successorOffsetRows[unit];
                 edge < successorOffsetRows[unit + 1];
                 edge++)
            {
                if (reachesComputeRows[successorRowValues[edge]] == 0) continue;
                reachesComputeRows[unit] = 1;
                break;
            }
        }
        for (int order = 0; order < orderedBuildUnits.Length; order++)
        {
            int unit = orderedUnitRows[order];
            if (unitRows[unit].Queue == QueueType.Compute)
            {
                followsComputeRows[unit] = 1;
                continue;
            }
            for (int edge = incomingOffsetRows[unit];
                 edge < incomingOffsetRows[unit + 1];
                 edge++)
            {
                if (followsComputeRows[incomingRowValues[edge]] == 0) continue;
                followsComputeRows[unit] = 1;
                break;
            }
        }

        ArenaSlice<byte> classes =
            graph.AllocateSlice<byte>(unitCount, clear: false);
        byte* classRows = classes.DangerousPointer;
        Span<int> classCounts = stackalloc int[4];
        for (int unit = 0; unit < unitCount; unit++)
        {
            byte unitClass;
            if (unitRows[unit].Queue == QueueType.Compute)
            {
                unitClass = 2;
            }
            else
            {
                bool before = reachesComputeRows[unit] != 0;
                bool after = followsComputeRows[unit] != 0;
                if (before && after) return false;
                unitClass = before
                    ? (byte)0
                    : after
                        ? (byte)3
                        : (byte)1;
            }
            classRows[unit] = unitClass;
            classCounts[unitClass]++;
        }

        for (int predecessor = 0; predecessor < unitCount; predecessor++)
        for (int edge = successorOffsetRows[predecessor];
             edge < successorOffsetRows[predecessor + 1];
             edge++)
        {
            if (classRows[predecessor] >
                classRows[successorRowValues[edge]])
                return false;
        }

        Span<int> classOffsets = stackalloc int[4];
        Span<int> classCursors = stackalloc int[4];
        Span<int> classBatchOrdinals = stackalloc int[4];
        int runningOffset = 0;
        int runningBatch = graph.CommandBatches.Count;
        for (byte unitClass = 0; unitClass < 4; unitClass++)
        {
            int count = classCounts[unitClass];
            classOffsets[unitClass] = runningOffset;
            classCursors[unitClass] = runningOffset;
            classBatchOrdinals[unitClass] =
                count == 0 ? -1 : runningBatch++;
            runningOffset = checked(runningOffset + count);
        }

        int batchUnitBase = graph.BatchRuntimeCmds.Count;
        Span<int> partitionedUnits =
            graph.BatchRuntimeCmds.AddUninitialized(unitCount);
        for (int order = 0; order < unitCount; order++)
        {
            int unit = orderedUnitRows[order];
            byte unitClass = classRows[unit];
            int destination = classCursors[unitClass]++;
            partitionedUnits[destination] = unit;
            unitBatchRows[unit] = classBatchOrdinals[unitClass];
        }

        for (byte unitClass = 0; unitClass < 4; unitClass++)
        {
            int count = classCounts[unitClass];
            if (count == 0) continue;
            QueueType queue =
                unitClass == 2 ? QueueType.Compute : QueueType.Graphics;
            graph.CommandBatches.Add(new CommandBatch(
                queue,
                0,
                0,
                checked(batchUnitBase + classOffsets[unitClass]),
                count,
                0,
                0,
                0,
                0));
        }
        return true;
    }

    private static unsafe bool AppendBatchResource(
        RenderGraph graph,
        int resource,
        int stamp,
        int* resourceMarks)
    {
        if (resourceMarks[resource] == stamp) return false;
        resourceMarks[resource] = stamp;
        graph.BatchResourceRows.Add(resource);
        return true;
    }

    private static unsafe void AppendExternalWaits(
        RenderGraph graph,
        int resource,
        QueueType queue,
        byte* seenOnQueue,
        Span<QueueCompletion> waits,
        Span<ulong> establishedWaitValues)
    {
        if (!graph.IsResourceImported(resource)) return;
        int seenIndex = checked(resource * 3 + (int)queue);
        if (seenOnQueue[seenIndex] != 0) return;
        seenOnQueue[seenIndex] = 1;
        foreach (QueueCompletion readiness in graph.GetResourceReadiness(resource))
        {
            if (readiness.Queue.Type == queue) continue;
            int queueIndex = (int)readiness.Queue.Type;
            int establishedIndex =
                checked((int)queue * 3 + queueIndex);
            if (readiness.Value <=
                establishedWaitValues[establishedIndex])
            {
                continue;
            }
            if (waits[queueIndex] == default ||
                readiness.Value > waits[queueIndex].Value)
            {
                waits[queueIndex] = readiness;
                establishedWaitValues[establishedIndex] =
                    readiness.Value;
            }
        }
    }

    private static bool IntroducesCrossQueueReadiness(
        RenderGraph graph,
        int pass,
        QueueType queue,
        ArenaSlice<byte> seenImportedResources)
    {
        foreach (ref readonly PassInputData access in graph.GetPassAccesses(graph.Passes[pass]))
        {
            int resource = graph.GetResourceOrdinal(access);
            if (!graph.IsResourceImported(resource) || seenImportedResources[resource] != 0) continue;
            ReadOnlySpan<QueueCompletion> readiness = graph.GetResourceReadiness(resource);
            foreach (QueueCompletion completion in readiness)
                if (completion.Queue.Type != queue) return true;
        }
        return false;
    }

    private static void MarkImportedResources(
        RenderGraph graph,
        int pass,
        ArenaSlice<byte> seenImportedResources)
    {
        foreach (ref readonly PassInputData access in graph.GetPassAccesses(graph.Passes[pass]))
        {
            int resource = graph.GetResourceOrdinal(access);
            if (graph.IsResourceImported(resource)) seenImportedResources[resource] = 1;
        }
    }

    private static bool RequiresCoordinator(RenderGraph graph, ReadOnlySpan<int> logicalPasses)
    {
        foreach (int pass in logicalPasses)
        {
            if ((graph.Passes[pass].Flags & PassFlags.NeverParallel) != 0) return true;
        }
        return false;
    }

    private static ReadOnlySpan<int> GetPasses(
        ArenaColumn<int> passRows,
        ArenaColumn<int> groupStarts,
        int group)
    {
        int offset = groupStarts[group];
        return passRows.GetReadOnlySpan(
            offset,
            groupStarts[group + 1] - offset);
    }

    private static unsafe ArenaSlice<Extent2D> BuildExtent2Ds(
        RenderGraph graph,
        ArenaSlice<QueueType> queues)
    {
        int passCount = graph.Passes.Length;
        PassData* canonicalPassRows =
            graph.Passes.DangerousContiguousPointer;
        PassInputData* canonicalPassInputs =
            graph.PassInputs.DangerousContiguousPointer;
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        QueueType* queueRows = queues.DangerousPointer;
        bool hasRenderingAttachments = false;
        for (int pass = 0; pass < passCount; pass++)
        {
            ref readonly PassData value = ref (
                canonicalPassRows is not null
                    ? ref canonicalPassRows[pass]
                    : ref graph.Passes[pass]);
            if (value.ColorAttachmentCount == 0 && value.DepthStencilAttachmentOrdinal < 0) continue;
            hasRenderingAttachments = true;
            break;
        }
        if (!hasRenderingAttachments) return default;

        ArenaSlice<Extent2D> result =
            graph.AllocateSlice<Extent2D>(passCount);

        int maximumAccessCount = 0;
        for (int pass = 0; pass < passCount; pass++)
        {
            int count = canonicalPassRows is not null
                ? canonicalPassRows[pass].AccessCount
                : graph.Passes[pass].AccessCount;
            maximumAccessCount = Math.Max(maximumAccessCount, count);
        }
        ArenaSlice<int> attachmentAccessMarks = graph.AllocateSlice<int>(maximumAccessCount);
        for (int passIndex = 0; passIndex < passCount; passIndex++)
        {
            ref readonly PassData pass = ref (
                canonicalPassRows is not null
                    ? ref canonicalPassRows[passIndex]
                    : ref graph.Passes[passIndex]);
            string passName = graph.GetPassName(passIndex);
            ReadOnlySpan<PassInputData> accesses =
                canonicalPassInputs is not null
                    ? new ReadOnlySpan<PassInputData>(
                        canonicalPassInputs + pass.AccessOffset,
                        pass.AccessCount)
                    : graph.GetPassAccesses(pass);
            ReadOnlySpan<PassFragmentData> colors = graph.GetPassColorAttachments(pass);
            if (colors.Length == 0 && pass.DepthStencilAttachmentOrdinal < 0)
                continue;

            if ((liveFlags[passIndex] &
                 RenderGraph.PassLiveFlag) != 0 &&
                queueRows[passIndex] != QueueType.Graphics)
                throw new InvalidOperationException($"Pass '{passName}' declares rendering attachments but does not select the graphics queue.");

            int width = 0;
            int height = 0;
            int sampleCount = 0;
            int attachmentStamp = passIndex + 1;
            for (int colorIndex = 0; colorIndex < colors.Length; colorIndex++)
            {
                PassFragmentData color = colors[colorIndex];
                if (color.Slot != colorIndex)
                    throw new InvalidOperationException($"Pass '{passName}' color attachment slots must be unique and contiguous starting at zero.");
                if ((uint)color.View >= (uint)graph.TextureViewCount)
                    throw new InvalidOperationException($"Pass '{passName}' references an invalid color attachment view ordinal.");
                if ((uint)color.Access >= (uint)accesses.Length)
                    throw new InvalidOperationException($"Pass '{passName}' references an invalid color attachment access ordinal.");
                if (!Enum.IsDefined(color.Load))
                    throw new InvalidOperationException($"Pass '{passName}' has an invalid color attachment load action.");
                if (attachmentAccessMarks[color.Access] == attachmentStamp)
                    throw new InvalidOperationException($"Pass '{passName}' reuses one texture access for multiple color attachment slots.");
                attachmentAccessMarks[color.Access] = attachmentStamp;

                int viewResource = graph.GetTextureViewResource(color.View);
                TextureSubresourceRange viewRange = graph.GetTextureViewRange(color.View);
                PassInputData access = graph.GetDeclaredAccess(passIndex, color.Access);
                GraphAccess expectedFlags = color.Load == LoadType.Load
                    ? GraphAccess.Write
                    : GraphAccess.WriteAll;
                if (access.IsBuffer || access.Texture != viewResource || access.View != color.View ||
                    access.State != GraphResourceUsage.RenderTarget || access.Flags != expectedFlags)
                {
                    throw new InvalidOperationException($"Pass '{passName}' color attachment metadata does not match its canonical texture access.");
                }
                if ((graph.GetTextureViewUsage(color.View) &
                     GraphTextureViewUsage.ColorAttachment) == 0)
                    throw new InvalidOperationException($"Pass '{passName}' color attachment view lacks GraphAttachmentPlan usage.");
                if (viewRange.MipLevelCount != 1 ||
                    viewRange.ArrayLayerCount != 1 ||
                    viewRange.Aspects != TextureAspects.Color)
                    throw new InvalidOperationException($"Pass '{passName}' color attachment views must select exactly one color mip and one array layer.");

                GraphTextureDescription desc = graph.GetTextureDescription(viewResource);
                if ((desc.Usages & TextureUsages.ColorAttachment) == 0)
                    throw new InvalidOperationException($"Pass '{passName}' color attachment resource lacks GraphAttachmentPlan usage.");
                if (GraphFormat.IsDepth(desc.Format) ||
                    graph.GetTextureViewFormat(color.View) != desc.Format)
                    throw new InvalidOperationException($"Pass '{passName}' color attachment requires an exact non-depth view format.");
                if (desc.Depth != 1)
                    throw new NotSupportedException("Three-dimensional color attachments are not supported by the render graph.");

                if (color.HasResolve)
                {
                    if ((uint)color.ResolveView >= (uint)graph.TextureViewCount ||
                        (uint)color.ResolveAccess >= (uint)accesses.Length)
                    {
                        throw new InvalidOperationException($"Pass '{passName}' references an invalid integrated resolve declaration.");
                    }
                    if (attachmentAccessMarks[color.ResolveAccess] == attachmentStamp)
                        throw new InvalidOperationException($"Pass '{passName}' reuses one texture access for multiple attachment operations.");
                    attachmentAccessMarks[color.ResolveAccess] = attachmentStamp;
                    int resolveResource =
                        graph.GetTextureViewResource(color.ResolveView);
                    TextureSubresourceRange resolveRange =
                        graph.GetTextureViewRange(color.ResolveView);
                    PassInputData resolveAccess = graph.GetDeclaredAccess(passIndex, color.ResolveAccess);
                    if (resolveAccess.IsBuffer ||
                        resolveAccess.Texture != resolveResource ||
                        resolveAccess.View != color.ResolveView ||
                        resolveAccess.State != GraphResourceUsage.ResolveDestination ||
                        resolveAccess.Flags != GraphAccess.WriteAll)
                    {
                        throw new InvalidOperationException($"Pass '{passName}' integrated resolve metadata does not match its declared destination access.");
                    }
                    if (resolveRange.MipLevelCount != 1 ||
                        resolveRange.ArrayLayerCount != 1 ||
                        resolveRange.Aspects != TextureAspects.Color)
                    {
                        throw new InvalidOperationException($"Pass '{passName}' integrated resolve destination must select one color subresource.");
                    }
                    if ((graph.GetTextureViewUsage(color.ResolveView) &
                         GraphTextureViewUsage.ResolveDestination) == 0)
                        throw new InvalidOperationException($"Pass '{passName}' integrated resolve view lacks ResolveDestination usage.");
                    if (resolveResource == viewResource)
                        throw new InvalidOperationException($"Pass '{passName}' integrated resolve source and destination must be different textures.");
                    try
                    {
                        GraphResolveValidation.Validate(
                            color.ResolveType,
                            GraphTextureAspect.Color,
                            viewRange.FirstMipLevel,
                            viewRange.FirstArrayLayer,
                            resolveRange.FirstMipLevel,
                            resolveRange.FirstArrayLayer,
                            desc,
                            graph.GetTextureDescription(resolveResource));
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                    {
                        throw new InvalidOperationException($"Pass '{passName}' has an invalid integrated color resolve.", exception);
                    }
                }

                int mipWidth = Math.Max(1, desc.Width >> checked((int)viewRange.FirstMipLevel));
                int mipHeight = Math.Max(1, desc.Height >> checked((int)viewRange.FirstMipLevel));
                if (sampleCount == 0)
                {
                    width = mipWidth;
                    height = mipHeight;
                    sampleCount = desc.SampleCount;
                }
                else if (width != mipWidth || height != mipHeight || sampleCount != desc.SampleCount)
                {
                    throw new InvalidOperationException($"Pass '{passName}' color attachments must have identical extent and sample count.");
                }
            }

            if (graph.GetPassDepthStencilAttachment(pass) is PassFragmentData depthStencil)
            {
                if ((uint)depthStencil.View >= (uint)graph.TextureViewCount)
                    throw new InvalidOperationException($"Pass '{passName}' references an invalid depth-stencil attachment view ordinal.");
                int viewResource = graph.GetTextureViewResource(depthStencil.View);
                TextureSubresourceRange viewRange = graph.GetTextureViewRange(depthStencil.View);
                if ((graph.GetTextureViewUsage(depthStencil.View) &
                     GraphTextureViewUsage.DepthStencilAttachment) == 0)
                    throw new InvalidOperationException($"Pass '{passName}' depth-stencil view lacks DepthStencilAttachment usage.");
                if (viewRange.MipLevelCount != 1 || viewRange.ArrayLayerCount != 1)
                    throw new InvalidOperationException($"Pass '{passName}' depth-stencil view must select exactly one mip and one array layer.");
                GraphTextureDescription desc = graph.GetTextureDescription(viewResource);
                if (!GraphFormat.IsDepth(desc.Format) ||
                    graph.GetTextureViewFormat(depthStencil.View) != desc.Format)
                    throw new InvalidOperationException($"Pass '{passName}' depth-stencil attachment requires an exact depth format.");
                if ((desc.Usages & TextureUsages.DepthStencilAttachment) == 0 || desc.Depth != 1)
                    throw new InvalidOperationException($"Pass '{passName}' depth-stencil attachment resource is not renderable.");

                ValidateDepthStencilPlane(
                    graph,
                    passIndex,
                    pass,
                    depthStencil,
                    depthStencil.View,
                    depthPlane: true,
                    attachmentAccessMarks,
                    attachmentStamp);
                ValidateDepthStencilPlane(
                    graph,
                    passIndex,
                    pass,
                    depthStencil,
                    depthStencil.View,
                    depthPlane: false,
                    attachmentAccessMarks,
                    attachmentStamp);
                if (!GraphFormat.HasStencil(desc.Format) && depthStencil.HasStencil)
                    throw new InvalidOperationException($"Pass '{passName}' cannot attach a stencil plane to {desc.Format}.");

                int mipWidth = Math.Max(1, desc.Width >> checked((int)viewRange.FirstMipLevel));
                int mipHeight = Math.Max(1, desc.Height >> checked((int)viewRange.FirstMipLevel));
                if (sampleCount == 0)
                {
                    width = mipWidth;
                    height = mipHeight;
                    sampleCount = desc.SampleCount;
                }
                else if (width != mipWidth || height != mipHeight || sampleCount != desc.SampleCount)
                {
                    throw new InvalidOperationException($"Pass '{passName}' color and depth-stencil attachments must have identical extent and sample count.");
                }
            }
            ReadOnlySpan<PassInputData> passAccesses = graph.GetPassAccesses(pass);
            for (int accessIndex = 0; accessIndex < passAccesses.Length; accessIndex++)
            {
                PassInputData access = passAccesses[accessIndex];
                if (!access.IsBuffer &&
                    access.State is GraphResourceUsage.RenderTarget or GraphResourceUsage.DepthRead or GraphResourceUsage.DepthWrite or GraphResourceUsage.ResolveDestination &&
                    attachmentAccessMarks[accessIndex] != attachmentStamp)
                {
                    throw new InvalidOperationException($"Pass '{passName}' has an attachment access without specialized attachment metadata.");
                }
            }
            if ((liveFlags[passIndex] &
                 RenderGraph.PassLiveFlag) != 0)
            {
                result[passIndex] =
                    new Extent2D(width, height);
            }
        }
        return result;
    }

    private static void ValidateDepthStencilPlane(
        RenderGraph graph,
        int passOrdinal,
        in PassData pass,
        in PassFragmentData attachment,
        int view,
        bool depthPlane,
        ArenaSlice<int> attachmentAccessMarks,
        int attachmentStamp)
    {
        string passName = graph.GetPassName(passOrdinal);
        int accessOrdinal = depthPlane ? attachment.DepthAccess : attachment.StencilAccess;
        bool present = depthPlane ? attachment.HasDepth : attachment.HasStencil;
        GraphTextureAspect plane = depthPlane ? GraphTextureAspect.Depth : GraphTextureAspect.Stencil;
        TextureAspects planes = depthPlane ? TextureAspects.Depth : TextureAspects.Stencil;
        if (!present)
        {
            if (accessOrdinal != -1)
                throw new InvalidOperationException($"Pass '{passName}' has an access ordinal for an absent {plane} attachment plane.");
            return;
        }
        ReadOnlySpan<PassInputData> accesses = graph.GetPassAccesses(pass);
        TextureSubresourceRange viewRange = graph.GetTextureViewRange(view);
        if ((viewRange.Aspects & planes) == 0 ||
            (uint)accessOrdinal >= (uint)accesses.Length)
            throw new InvalidOperationException($"Pass '{passName}' has invalid {plane} attachment metadata.");
        if (attachmentAccessMarks[accessOrdinal] == attachmentStamp)
            throw new InvalidOperationException($"Pass '{passName}' reuses one access for multiple attachment planes.");
        attachmentAccessMarks[accessOrdinal] = attachmentStamp;

        LoadType load = depthPlane ? attachment.DepthLoad : attachment.StencilLoad;
        bool readOnly = depthPlane ? attachment.DepthReadOnly : attachment.StencilReadOnly;

        PassInputData access = graph.GetDeclaredAccess(passOrdinal, accessOrdinal);
        GraphAccess expectedFlags = readOnly
            ? GraphAccess.Read
            : load == LoadType.Load
                ? GraphAccess.Write
                : GraphAccess.WriteAll;
        GraphResourceUsage expectedUse = readOnly ? GraphResourceUsage.DepthRead : GraphResourceUsage.DepthWrite;
        if (access.IsBuffer ||
            access.Texture != graph.GetTextureViewResource(view) ||
            access.View != attachment.View ||
            access.State != expectedUse || access.Flags != expectedFlags ||
            access.TextureRange != (viewRange with { Aspects = planes }))
        {
            throw new InvalidOperationException($"Pass '{passName}' {plane} attachment metadata does not match its canonical texture access.");
        }
    }

    private static void ValidateResourceUsage(RenderGraph graph)
    {
        for (int resource = 0; resource < graph.Buffers.Count; resource++)
        {
            ResourceUnversionedData buffer = graph.Buffers[resource];
            BufferDesc description = graph.GetBufferDescription(resource);
            bool accelerationStructure =
                (description.Usages & BufferUsages.AccelerationStructure) != 0;
            if (accelerationStructure && buffer.MemoryType != MemoryType.DeviceLocal)
                throw new InvalidOperationException("Acceleration-structure buffers must use device-local memory.");
            if (!buffer.IsImported) continue;
            bool used = graph.IsResourceLive(resource);
            RequireBufferState(description, buffer.InitialState, "initial import");
            RequireBufferState(description, buffer.FinalState, "final import");
            if (accelerationStructure &&
                (InitialState(graph, resource) != GraphResourceUsage.AccelerationStructure ||
                 FinalState(graph, resource) != GraphResourceUsage.AccelerationStructure))
            {
                throw new InvalidOperationException(
                    "Imported acceleration-structure buffers must remain in AccelerationStructure state for their entire lifetime.");
            }
            if (!used && InitialState(graph, resource) != FinalState(graph, resource))
                throw new InvalidOperationException("An unused imported buffer cannot establish a different final use because no submission owns the transition.");
        }
        for (int texture = 0; texture < graph.Textures.Count; texture++)
        {
            int resource = graph.GetTextureResourceOrdinal(texture);
            ResourceUnversionedData row = graph.Textures[texture];
            if (!row.IsImported) continue;
            RequireTextureState(
                graph.GetTextureDescription(texture),
                row.InitialState,
                "initial import");
            RequireTextureState(
                graph.GetTextureDescription(texture),
                row.FinalState,
                "final import");
            if (!graph.IsResourceLive(resource) && InitialState(graph, resource) != FinalState(graph, resource))
                throw new InvalidOperationException("An unused imported texture cannot establish a different final use because no submission owns the transition.");
        }
    }

    private static void ValidatePassAccessUsage(
        RenderGraph graph,
        int pass,
        QueueType queue,
        in PassInputData access)
    {
        ValidatePassAccessResourceUsage(graph, pass, access);
        ValidatePassAccessQueueUsage(graph, pass, queue, access);
    }

    private static void ValidatePassAccessResourceUsage(
        RenderGraph graph,
        int pass,
        in PassInputData access)
    {
        if (access.IsBuffer)
        {
            if (access.View < 0)
                RequireBufferUsage(
                    graph.GetBufferDescription(access.Buffer),
                    access.State,
                    graph.GetPassName(pass),
                    passOwner: true);
            return;
        }

        if (access.View < 0)
            RequireTextureUsage(
                graph.GetTextureDescription(access.Texture),
                access.State,
                graph.GetPassName(pass),
                passOwner: true);
    }

    private static void ValidatePassAccessQueueUsage(
        RenderGraph graph,
        int pass,
        QueueType queue,
        in PassInputData access)
    {
        if (!graph.IsPassLive(pass)) return;
        if (access.IsBuffer)
        {
            if (!QueueSupports(queue, access.State))
                throw new InvalidOperationException($"Pass '{graph.GetPassName(pass)}' selects {queue} but declares buffer use {access.State}.");
            return;
        }
        if (!QueueSupports(queue, access.State))
            throw new InvalidOperationException($"Pass '{graph.GetPassName(pass)}' selects {queue} but declares texture use {access.State}.");
    }

    private static void RequireBufferUsage(
        in BufferDesc desc,
        GraphResourceUsage use,
        string owner,
        bool passOwner = false)
    {
        BufferUsages required = use switch
        {
            GraphResourceUsage.CopySource => BufferUsages.CopySource,
            GraphResourceUsage.CopyDestination => BufferUsages.CopyDestination,
            GraphResourceUsage.ShaderResource => BufferUsages.ShaderRead,
            GraphResourceUsage.UnorderedAccess => BufferUsages.ShaderWrite,
            GraphResourceUsage.VertexOrConstantBuffer => BufferUsages.Vertex | BufferUsages.Constant,
            GraphResourceUsage.IndexBuffer => BufferUsages.Index,
            GraphResourceUsage.IndirectArgument => BufferUsages.Indirect,
            GraphResourceUsage.AccelerationStructure => BufferUsages.AccelerationStructure,
            _ => throw new ArgumentOutOfRangeException(nameof(use)),
        };
        bool available = use == GraphResourceUsage.VertexOrConstantBuffer
            ? (desc.Usages & required) != 0
            : (desc.Usages & required) == required;
        if (!available)
        {
            string ownerDescription = passOwner ? $"pass '{owner}'" : owner;
            throw new InvalidOperationException($"{ownerDescription} requires buffer usage {use}, but '{desc.Label ?? "unnamed"}' was created with {desc.Usages}.");
        }
    }

    private static void RequireBufferState(in BufferDesc desc, GraphResourceUsage state, string owner)
    {
        if (state == GraphResourceUsage.Common) return;
        GraphResourceUsage use = state switch
        {
            GraphResourceUsage.CopySource => GraphResourceUsage.CopySource,
            GraphResourceUsage.CopyDestination => GraphResourceUsage.CopyDestination,
            GraphResourceUsage.ShaderResource => GraphResourceUsage.ShaderResource,
            GraphResourceUsage.UnorderedAccess => GraphResourceUsage.UnorderedAccess,
            GraphResourceUsage.VertexOrConstantBuffer => GraphResourceUsage.VertexOrConstantBuffer,
            GraphResourceUsage.IndexBuffer => GraphResourceUsage.IndexBuffer,
            GraphResourceUsage.IndirectArgument => GraphResourceUsage.IndirectArgument,
            GraphResourceUsage.AccelerationStructure => GraphResourceUsage.AccelerationStructure,
            _ => throw new InvalidOperationException($"{owner} state {state} is not valid for a buffer."),
        };
        RequireBufferUsage(desc, use, owner);
    }

    private static void RequireTextureUsage(
        in GraphTextureDescription desc,
        GraphResourceUsage use,
        string owner,
        bool passOwner = false)
    {
        TextureUsages required = use switch
        {
            GraphResourceUsage.CopySource => TextureUsages.CopySource,
            GraphResourceUsage.CopyDestination => TextureUsages.CopyDestination,
            GraphResourceUsage.ResolveSource => TextureUsages.CopySource,
            GraphResourceUsage.ResolveDestination => TextureUsages.CopyDestination,
            GraphResourceUsage.ShaderResource => TextureUsages.Sampled,
            GraphResourceUsage.UnorderedAccess => TextureUsages.Storage,
            GraphResourceUsage.RenderTarget => TextureUsages.ColorAttachment,
            GraphResourceUsage.DepthRead or GraphResourceUsage.DepthWrite => TextureUsages.DepthStencilAttachment,
            GraphResourceUsage.ShadingRateSource => TextureUsages.ShadingRate,
            _ => throw new ArgumentOutOfRangeException(nameof(use)),
        };
        if ((desc.Usages & required) != required)
            throw new InvalidOperationException($"{DescribeOwner(owner, passOwner)} requires texture usage {use}, but '{desc.Label ?? "unnamed"}' was created with {desc.Usages}.");

        bool depth = GraphFormat.IsDepth(desc.Format);
        if (use == GraphResourceUsage.RenderTarget && depth)
            throw new InvalidOperationException($"{DescribeOwner(owner, passOwner)} cannot use depth format {desc.Format} as a color attachment.");
        if (use is GraphResourceUsage.DepthRead or GraphResourceUsage.DepthWrite && !depth)
            throw new InvalidOperationException($"{DescribeOwner(owner, passOwner)} cannot use color format {desc.Format} as a depth attachment.");
        if (use == GraphResourceUsage.UnorderedAccess && depth)
            throw new NotSupportedException("Storage access to depth/stencil formats is not supported by the graphics interface.");
        if (use == GraphResourceUsage.ShadingRateSource && (desc.Format != Format.R8UInt || desc.Dimension != TextureDimension.Texture2D ||
            desc.MipLevels != 1 || desc.ArrayLayers != 1 || desc.SampleCount != 1))
            throw new InvalidOperationException("A shading-rate image must be a single-mip, single-layer, single-sample R8UInt 2D texture.");
    }

    private static void RequireTextureState(in GraphTextureDescription desc, GraphResourceUsage state, string owner)
    {
        if (state is GraphResourceUsage.Common or
            GraphResourceUsage.Undefined or
            GraphResourceUsage.Present)
            return;
        if (state == GraphResourceUsage.DepthReadShaderResource)
        {
            RequireTextureUsage(desc, GraphResourceUsage.DepthRead, owner);
            RequireTextureUsage(desc, GraphResourceUsage.ShaderResource, owner);
            return;
        }
        GraphResourceUsage use = state switch
        {
            GraphResourceUsage.CopySource => GraphResourceUsage.CopySource,
            GraphResourceUsage.CopyDestination => GraphResourceUsage.CopyDestination,
            GraphResourceUsage.ShaderResource => GraphResourceUsage.ShaderResource,
            GraphResourceUsage.UnorderedAccess => GraphResourceUsage.UnorderedAccess,
            GraphResourceUsage.RenderTarget => GraphResourceUsage.RenderTarget,
            GraphResourceUsage.DepthWrite => GraphResourceUsage.DepthWrite,
            GraphResourceUsage.DepthRead => GraphResourceUsage.DepthRead,
            GraphResourceUsage.ResolveSource => GraphResourceUsage.ResolveSource,
            GraphResourceUsage.ResolveDestination => GraphResourceUsage.ResolveDestination,
            GraphResourceUsage.ShadingRateSource => GraphResourceUsage.ShadingRateSource,
            _ => throw new InvalidOperationException($"{owner} state {state} is not valid for a texture."),
        };
        RequireTextureUsage(desc, use, owner);
    }

    private static string DescribeOwner(string owner, bool passOwner) =>
        passOwner ? $"pass '{owner}'" : owner;

    private static bool QueueSupports(QueueType queue, GraphResourceUsage use) => queue switch
    {
        QueueType.Graphics => true,
        QueueType.Compute => use is GraphResourceUsage.CopySource or GraphResourceUsage.CopyDestination or GraphResourceUsage.ShaderResource or GraphResourceUsage.UnorderedAccess or GraphResourceUsage.VertexOrConstantBuffer or GraphResourceUsage.IndirectArgument or GraphResourceUsage.AccelerationStructure,
        QueueType.Copy => use is GraphResourceUsage.CopySource or GraphResourceUsage.CopyDestination,
        _ => false,
    };

    private static void BuildDependenciesAndBarriers(
        RenderGraph graph,
        Device device,
        ArenaSlice<QueueType> queues,
        ArenaSlice<int> accessPassOrdinals,
        ArenaSlice<int> resourceAccessOffsets,
        ArenaSlice<int> resourceAccessOrdinals,
        out PassBarrierTable commandUnitBarriers,
        out PassPredecessorTable commandUnitBarrierPredecessors)
    {
        graph.DependencyRows.Clear();
        int passCount = graph.Passes.Length;
        int resourceCount = graph.ResourceCount;
        ArenaSlice<int> historyOffsets =
            graph.AllocateSlice<int>(checked(resourceCount + 1), clear: false);
        int historyCount = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            historyOffsets[resource] = historyCount;
            if (!graph.IsResourceLive(resource)) continue;
            if (graph.IsBufferResourceOrdinal(resource))
            {
                int segmentCount = Math.Max(
                    0,
                    graph.BufferBoundaries[graph.GetBufferOrdinal(resource)].Count - 1);
                historyCount = checked(historyCount + segmentCount);
            }
            else
            {
                GraphTextureDescription texture =
                    graph.GetTextureDescription(graph.GetTextureOrdinal(resource));
                int planes = GraphFormat.HasStencil(texture.Format) ? 2 : 1;
                historyCount = checked(
                    historyCount +
                    checked(texture.MipLevels * texture.ArrayLayers * planes));
            }
        }
        historyOffsets[resourceCount] = historyCount;
        ArenaSlice<AccessHistory> accessHistories =
            graph.AllocateSlice<AccessHistory>(historyCount, clear: false);
        accessHistories.Span.Fill(AccessHistory.Empty);
        ArenaSlice<ResourceQueueHistory> bufferQueues =
            graph.AllocateSlice<ResourceQueueHistory>(
                graph.Buffers.Length,
                clear: false);
        bufferQueues.Span.Fill(ResourceQueueHistory.Empty);
        ArenaSlice<int> dependencyMarks =
            graph.AllocateSlice<int>(passCount);
        ArenaSlice<int> dependencies =
            graph.AllocateSlice<int>(passCount, clear: false);

        PassBarrierTable afterTable = new(graph, passCount);
        commandUnitBarriers =
            new PassBarrierTable(graph, checked(passCount + 1));
        commandUnitBarrierPredecessors =
            new PassPredecessorTable(graph, checked(passCount + 1));
        int finalBarrierKey = passCount;
        int bufferCount = graph.Buffers.Length;
        int textureCount = graph.Textures.Length;
        ArenaSlice<GraphResourceUsage> bufferStates =
            graph.AllocateSlice<GraphResourceUsage>(bufferCount, clear: false);
        ArenaSlice<int> bufferLastPass =
            graph.AllocateSlice<int>(bufferCount, clear: false);
        bufferLastPass.Span.Fill(-1);
        ArenaSlice<GraphAccess> bufferLastEffect =
            graph.AllocateSlice<GraphAccess>(bufferCount, clear: false);
        ArenaSlice<TextureBarrierTracker> textureTrackers =
            graph.AllocateSlice<TextureBarrierTracker>(
                textureCount,
                clear: false);
        for (int buffer = 0; buffer < bufferCount; buffer++)
        {
            int resource =
                graph.GetBufferResourceOrdinal(buffer);
            bufferStates[buffer] = InitialState(graph, resource);
        }
        for (int texture = 0; texture < textureCount; texture++)
        {
            int resource =
                graph.GetTextureResourceOrdinal(texture);
            textureTrackers[texture] = new TextureBarrierTracker(
                graph,
                graph.GetTextureDescription(texture),
                InitialState(graph, resource));
        }

        for (int current = 0; current < passCount; current++)
        {
            ref PassData passCompilation = ref graph.Passes[current];
            passCompilation.DependencyOffset = graph.DependencyRows.Count;
            passCompilation.DependencyCount = 0;
            passCompilation.BeforeBarrierOffset =
                graph.BeforeResourceBarriers.Count;
            passCompilation.BeforeBarrierCount = 0;
            if (!graph.IsPassLive(current)) continue;

            int dependencyCount = 0;
            int stamp = checked(current + 1);
            QueueType queue = queues[current];
            ref readonly PassData passRow = ref graph.Passes[current];
            foreach (ref readonly PassInputData access in graph.GetPassAccesses(passRow))
            {
                int resource = graph.GetResourceOrdinal(access);
                if (access.IsBuffer)
                {
                    ref ResourceQueueHistory resourceQueue =
                        ref bufferQueues[access.Buffer];
                    AddCrossQueueDependencies(
                        resourceQueue,
                        queue,
                        current,
                        stamp,
                        graph,
                        dependencyMarks,
                        dependencies,
                        ref dependencyCount);
                    if (graph.IsResourceWritten(resource))
                    {
                        BufferBoundaryIndex boundaries =
                            graph.BufferBoundaries[access.Buffer];
                        ulong accessEnd =
                            checked(access.BufferRange.Offset +
                                    access.BufferRange.Size);
                        int first;
                        int afterLast;
                        if (boundaries.Count == 2 &&
                            boundaries[0] == access.BufferRange.Offset &&
                            boundaries[1] == accessEnd)
                        {
                            first = 0;
                            afterLast = 1;
                        }
                        else
                        {
                            first = boundaries.Find(access.BufferRange.Offset);
                            afterLast = boundaries.Find(accessEnd);
                        }
                        if (first < 0 || afterLast < 0)
                        {
                            throw new InvalidOperationException(
                                "Normalized buffer access boundaries are missing from dependency tracking.");
                        }
                        for (int segment = first;
                             segment < afterLast;
                             segment++)
                        {
                            ref AccessHistory history =
                                ref accessHistories[
                                    historyOffsets[resource] + segment];
                            AddHazardDependencies(
                                ref history,
                                access.Flags,
                                queue,
                                includeCrossQueueReads: false,
                                current,
                                stamp,
                                graph,
                                dependencyMarks,
                                dependencies,
                                ref dependencyCount);
                        }
                    }
                    resourceQueue.Set(queue, current);

                    GraphResourceUsage desired =
                        access.State;
                    int buffer = access.Buffer;
                    MemoryType bufferMemoryType =
                        graph.Buffers[buffer].MemoryType;
                    if ((bufferMemoryType == MemoryType.Upload &&
                         desired is not (
                             GraphResourceUsage.CopySource or
                             GraphResourceUsage.ShaderResource or
                             GraphResourceUsage.VertexOrConstantBuffer or
                             GraphResourceUsage.IndexBuffer or
                             GraphResourceUsage.IndirectArgument)) ||
                        (bufferMemoryType == MemoryType.Readback &&
                         desired != GraphResourceUsage.CopyDestination))
                    {
                        throw new InvalidOperationException(
                            $"Pass '{graph.GetPassName(current)}' requests {desired} for " +
                            $"fixed-state {bufferMemoryType} buffer {buffer} " +
                            $"('{graph.GetBufferDescription(buffer).Label ?? "<unnamed>"}').");
                    }
                    if (bufferMemoryType is MemoryType.Upload or MemoryType.Readback)
                    {
                        bufferStates[buffer] = desired;
                        bufferLastPass[buffer] = current;
                        bufferLastEffect[buffer] = access.Flags;
                        continue;
                    }
                    bool firstTransientAccess =
                        bufferLastPass[buffer] < 0 &&
                        !graph.Buffers[buffer].IsImported;
                    bool transfersQueue =
                        bufferLastPass[buffer] >= 0 &&
                        queues[bufferLastPass[buffer]] != queue;
                    if (firstTransientAccess || transfersQueue ||
                        bufferStates[buffer] != desired)
                    {
                        PlannedBarrier transition = PlannedBarrier.BufferTransition(
                            resource,
                            bufferStates[buffer],
                            desired,
                            firstTransientAccess);
                        if (transfersQueue || QueueSupportsBarrier(
                                queue,
                                transition.Before,
                                transition.After))
                        {
                            AddTransition(
                                transition,
                                current,
                                bufferLastPass[buffer],
                                queues,
                                graph,
                                ref afterTable);
                        }
                        else
                        {
                            commandUnitBarriers.Add(current, transition);
                            if (bufferLastPass[buffer] >= 0)
                            {
                                commandUnitBarrierPredecessors.Add(
                                    current,
                                    bufferLastPass[buffer]);
                            }
                        }
                        bufferStates[buffer] = desired;
                    }
                    else if (desired == GraphResourceUsage.UnorderedAccess &&
                             bufferLastPass[buffer] >= 0 &&
                             (bufferLastEffect[buffer] != GraphAccess.Read ||
                              access.Flags != GraphAccess.Read))
                    {
                        AddBeforeBarrier(
                            graph,
                            current,
                            PlannedBarrier.BufferUnorderedAccess(resource));
                    }
                    bufferLastPass[buffer] = current;
                    bufferLastEffect[buffer] = access.Flags;
                    continue;
                }

                TextureSubresourceRange range = access.TextureRange;
                GraphTextureDescription textureDescription =
                    graph.GetTextureDescription(access.Texture);
                int stencilPlaneOffset =
                    checked(textureDescription.MipLevels *
                            textureDescription.ArrayLayers);
                for (int layer = checked((int)range.FirstArrayLayer);
                     layer < checked((int)(range.FirstArrayLayer + range.ArrayLayerCount));
                     layer++)
                for (int mip = checked((int)range.FirstMipLevel);
                     mip < checked((int)(range.FirstMipLevel + range.MipLevelCount));
                     mip++)
                {
                    int index = checked(
                        mip + layer * textureDescription.MipLevels);
                    if ((range.Aspects &
                         (TextureAspects.Color | TextureAspects.Depth)) != 0)
                    {
                        ref AccessHistory history =
                            ref accessHistories[historyOffsets[resource] + index];
                        AddHazardDependencies(
                            ref history,
                            access.Flags,
                            queue,
                            includeCrossQueueReads: true,
                            current,
                            stamp,
                            graph,
                            dependencyMarks,
                            dependencies,
                            ref dependencyCount);
                    }
                    if ((range.Aspects & TextureAspects.Stencil) != 0)
                    {
                        ref AccessHistory history = ref accessHistories[
                            historyOffsets[resource] +
                            index +
                            stencilPlaneOffset];
                        AddHazardDependencies(
                            ref history,
                            access.Flags,
                            queue,
                            includeCrossQueueReads: true,
                            current,
                            stamp,
                            graph,
                            dependencyMarks,
                            dependencies,
                            ref dependencyCount);
                    }
                }

                GraphResourceUsage textureDesired =
                    DesiredState(graph, passRow, access);
                ref TextureBarrierTracker tracker =
                    ref textureTrackers[access.Texture];
                bool requiresUavOrdering = false;
                foreach (TextureCell cell in EnumerateCells(access.TextureRange))
                {
                    int index = cell.Index(textureDescription);
                    GraphResourceUsage previous = tracker.States[index];
                    bool firstTransientAccess =
                        tracker.LastPass[index] < 0 &&
                        !graph.Textures[access.Texture].IsImported;
                    bool transfersQueue =
                        tracker.LastPass[index] >= 0 &&
                        queues[tracker.LastPass[index]] != queue;
                    if (firstTransientAccess || transfersQueue ||
                        previous != textureDesired)
                    {
                        PlannedBarrier transition =
                            PlannedBarrier.TextureTransition(
                                resource,
                                previous,
                                textureDesired,
                                cell.Range,
                                firstTransientAccess);
                        if (transfersQueue || QueueSupportsBarrier(
                                queue,
                                transition.Before,
                                transition.After))
                        {
                            AddTransition(
                                transition,
                                current,
                                tracker.LastPass[index],
                                queues,
                                graph,
                                ref afterTable);
                        }
                        else
                        {
                            commandUnitBarriers.Add(current, transition);
                            if (tracker.LastPass[index] >= 0)
                            {
                                commandUnitBarrierPredecessors.Add(
                                    current,
                                    tracker.LastPass[index]);
                            }
                        }
                        tracker.States[index] = textureDesired;
                    }
                    else if (textureDesired ==
                                 GraphResourceUsage.UnorderedAccess &&
                             tracker.LastPass[index] >= 0 &&
                             (tracker.LastEffect[index] != GraphAccess.Read ||
                              access.Flags != GraphAccess.Read))
                    {
                        requiresUavOrdering = true;
                    }
                    tracker.LastPass[index] = current;
                    tracker.LastEffect[index] = access.Flags;
                }
                if (requiresUavOrdering)
                {
                    AddBeforeBarrier(
                        graph,
                        current,
                        PlannedBarrier.TextureUnorderedAccess(
                            resource,
                            access.TextureRange));
                }
            }

            Span<int> passDependencies =
                dependencies.Span[..dependencyCount];
            passDependencies.Sort();
            passCompilation.DependencyOffset = graph.DependencyRows.Count;
            passCompilation.DependencyCount = dependencyCount;
            foreach (int dependency in passDependencies)
                graph.DependencyRows.Add(dependency);
        }

        for (int resource = 0; resource < resourceCount; resource++)
        {
            if (!graph.IsResourceImported(resource)) continue;
            GraphResourceUsage final = FinalState(graph, resource);
            if (resource < bufferCount)
            {
                int buffer = resource;
                if (bufferLastPass[buffer] >= 0 &&
                    bufferStates[buffer] != final)
                {
                    PlannedBarrier transition = PlannedBarrier.BufferTransition(
                        resource,
                        bufferStates[buffer],
                        final);
                    int lastPass = bufferLastPass[buffer];
                    if (QueueSupportsBarrier(
                            queues[lastPass],
                            transition.Before,
                            transition.After))
                    {
                        afterTable.Add(lastPass, transition);
                    }
                    else
                    {
                        commandUnitBarriers.Add(
                            finalBarrierKey,
                            transition);
                        AddResourceUsePasses(
                            graph,
                            resource,
                            finalBarrierKey,
                            ref commandUnitBarrierPredecessors);
                    }
                }
                bufferStates[buffer] = final;
                continue;
            }

            int texture = resource - bufferCount;
            ref TextureBarrierTracker tracker =
                ref textureTrackers[texture];
            bool used = false;
            foreach (int lastPass in tracker.LastPass)
            {
                if (lastPass < 0) continue;
                used = true;
                break;
            }
            if (!used) continue;
            GraphTextureDescription description =
                graph.GetTextureDescription(texture);
            TextureSubresourceRange whole = new(
                0,
                checked((uint)description.MipLevels),
                0,
                checked((uint)description.ArrayLayers),
                PlanesFor(description.Format));
            bool addedCommandUnitBarrier = false;
            foreach (TextureCell cell in EnumerateCells(whole))
            {
                int index = cell.Index(description);
                if (tracker.States[index] == final) continue;
                PlannedBarrier transition = PlannedBarrier.TextureTransition(
                    resource,
                    tracker.States[index],
                    final,
                    cell.Range);
                int lastPass = tracker.LastPass[index];
                if (lastPass >= 0 &&
                    QueueSupportsBarrier(
                        queues[lastPass],
                        transition.Before,
                        transition.After))
                {
                    afterTable.Add(lastPass, transition);
                }
                else
                {
                    commandUnitBarriers.Add(
                        finalBarrierKey,
                        transition);
                    addedCommandUnitBarrier = true;
                }
            }
            if (addedCommandUnitBarrier)
            {
                AddResourceUsePasses(
                    graph,
                    resource,
                    finalBarrierKey,
                    ref commandUnitBarrierPredecessors);
            }
            tracker.States.Span.Fill(final);
        }

        graph.BufferFinalStates = bufferStates;
        graph.TextureFinalStateOffsets =
            graph.AllocateSlice<int>(textureCount + 1, clear: false);
        int textureStateCount = 0;
        for (int texture = 0; texture < textureCount; texture++)
        {
            graph.TextureFinalStateOffsets[texture] = textureStateCount;
            textureStateCount = checked(
                textureStateCount +
                textureTrackers[texture].States.Length);
        }
        graph.TextureFinalStateOffsets[textureCount] =
            textureStateCount;
        graph.TextureFinalStates =
            graph.AllocateSlice<GraphResourceUsage>(
                textureStateCount,
                clear: false);
        for (int texture = 0; texture < textureCount; texture++)
        {
            int offset = graph.TextureFinalStateOffsets[texture];
            textureTrackers[texture].States.ReadOnlySpan.CopyTo(
                graph.TextureFinalStates.Span.Slice(
                    offset,
                    textureTrackers[texture].States.Length));
        }
        afterTable.WriteTo(graph, before: false);
    }

    private static unsafe void BuildResourceIndexedDependenciesAndBarriers(
        RenderGraph graph,
        Device device,
        ArenaSlice<QueueType> queues,
        ArenaSlice<int> accessPassOrdinals,
        ArenaSlice<int> resourceAccessOffsets,
        ArenaSlice<int> resourceAccessOrdinals,
        out PassBarrierTable commandUnitBarriers,
        out PassPredecessorTable commandUnitBarrierPredecessors)
    {
        int passCount = graph.Passes.Length;
        int resourceCount = graph.ResourceCount;
        QueueType* queueRows = queues.DangerousPointer;
        int* accessPassRows = accessPassOrdinals.DangerousPointer;
        int* resourceAccessOffsetRows =
            resourceAccessOffsets.DangerousPointer;
        int* resourcePassInputs =
            resourceAccessOrdinals.DangerousPointer;
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        PassInputData* canonicalPassInputs =
            graph.PassInputs.DangerousContiguousPointer;
        PassData* canonicalPassRows =
            graph.Passes.DangerousContiguousPointer;
        ResourceUnversionedData* canonicalBufferRows =
            graph.Buffers.DangerousContiguousPointer;
        ResourceUnversionedData* canonicalTextureRows =
            graph.Textures.DangerousContiguousPointer;
        BufferBoundaryIndex* bufferBoundaryRows =
            graph.BufferBoundaries.DangerousPointer;
        PassPredecessorTable dependencyTable =
            new(graph, passCount);
        int bufferCount = graph.Buffers.Length;
        int textureCount = graph.Textures.Length;

        ArenaSlice<int> historyOffsets =
            graph.AllocateSlice<int>(checked(resourceCount + 1), clear: false);
        int* historyOffsetRows = historyOffsets.DangerousPointer;
        int historyCount = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            historyOffsetRows[resource] = historyCount;
            if ((liveFlags[passCount + resource] &
                 RenderGraph.ResourceLiveFlag) == 0)
            {
                continue;
            }
            if (resource < bufferCount)
            {
                int segmentCount = Math.Max(
                    0,
                    bufferBoundaryRows[resource].Count - 1);
                historyCount = checked(historyCount + segmentCount);
            }
            else
            {
                GraphTextureDescription texture =
                    graph.GetTextureDescription(resource - bufferCount);
                int planes = GraphFormat.HasStencil(texture.Format) ? 2 : 1;
                historyCount = checked(
                    historyCount +
                    checked(texture.MipLevels * texture.ArrayLayers * planes));
            }
        }
        historyOffsetRows[resourceCount] = historyCount;
        ArenaSlice<AccessHistory> accessHistories =
            graph.AllocateSlice<AccessHistory>(historyCount, clear: false);
        accessHistories.Span.Fill(AccessHistory.Empty);
        AccessHistory* historyRows =
            accessHistories.DangerousPointer;

        int accessCount = graph.PassInputs.Length;
        PassBarrierTable beforeTable =
            new(graph, passCount, accessCount);
        PassBarrierTable afterTable =
            new(graph, passCount, accessCount);
        commandUnitBarriers = new PassBarrierTable(
            graph,
            checked(passCount + 1),
            graph.ResourceCount);
        commandUnitBarrierPredecessors = new PassPredecessorTable(
            graph,
            checked(passCount + 1));
        int finalBarrierKey = passCount;

        ArenaSlice<GraphResourceUsage> bufferStates =
            graph.AllocateSlice<GraphResourceUsage>(bufferCount, clear: false);
        ArenaSlice<int> bufferLastPass =
            graph.AllocateSlice<int>(bufferCount, clear: false);
        bufferLastPass.Span.Fill(-1);
        ArenaSlice<GraphAccess> bufferLastEffect =
            graph.AllocateSlice<GraphAccess>(bufferCount, clear: false);
        ArenaSlice<TextureBarrierTracker> textureTrackers =
            graph.AllocateSlice<TextureBarrierTracker>(textureCount, clear: false);
        GraphResourceUsage* bufferStateRows =
            bufferStates.DangerousPointer;
        int* bufferLastPassRows =
            bufferLastPass.DangerousPointer;
        GraphAccess* bufferLastEffectRows =
            bufferLastEffect.DangerousPointer;
        TextureBarrierTracker* textureTrackerRows =
            textureTrackers.DangerousPointer;

        for (int buffer = 0; buffer < bufferCount; buffer++)
        {
            bufferStateRows[buffer] = InitialState(graph, buffer);
        }
        for (int texture = 0; texture < textureCount; texture++)
        {
            textureTrackerRows[texture] = new TextureBarrierTracker(
                graph,
                graph.GetTextureDescription(texture),
                InitialState(graph, bufferCount + texture));
        }

        long finalBarrierOrder = 1L << 62;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            int firstResourceAccess = resourceAccessOffsetRows[resource];
            int afterLastResourceAccess =
                resourceAccessOffsetRows[resource + 1];
            if (graph.IsBufferResourceOrdinal(resource))
            {
                int buffer = graph.GetBufferOrdinal(resource);
                bool bufferImported = canonicalBufferRows is not null
                    ? canonicalBufferRows[buffer].IsImported
                    : graph.Buffers[buffer].IsImported;
                bool resourceWritten =
                    (liveFlags[passCount + resource] &
                     RenderGraph.ResourceWrittenFlag) != 0;
                ResourceQueueHistory queueHistory = ResourceQueueHistory.Empty;
                for (int index = firstResourceAccess;
                     index < afterLastResourceAccess;
                     index++)
                {
                    int accessOrdinal = resourcePassInputs[index];
                    int pass = accessPassRows[accessOrdinal];
                    if ((liveFlags[pass] & RenderGraph.PassLiveFlag) == 0)
                        continue;
                    ref readonly PassInputData access = ref (
                        canonicalPassInputs is not null
                            ? ref canonicalPassInputs[accessOrdinal]
                            : ref graph.PassInputs[accessOrdinal]);
                    if (!access.IsBuffer || access.Buffer != buffer)
                    {
                        throw new InvalidOperationException(
                            $"The resource access index mapped canonical access {accessOrdinal} " +
                            $"to buffer {buffer}, but the access targets " +
                            $"{(access.IsBuffer ? $"buffer {access.Buffer}" : $"texture {access.Texture}")}.");
                    }
                    QueueType queue = queueRows[pass];
                    AddCrossQueueDependencyBits(
                        queueHistory,
                        queue,
                        pass,
                        liveFlags,
                        ref dependencyTable);
                    if (resourceWritten)
                    {
                        BufferBoundaryIndex boundaries =
                            bufferBoundaryRows[buffer];
                        ulong accessEnd =
                            checked(access.BufferRange.Offset + access.BufferRange.Size);
                        int first;
                        int afterLast;
                        if (boundaries.Count == 2 &&
                            boundaries[0] == access.BufferRange.Offset &&
                            boundaries[1] == accessEnd)
                        {
                            first = 0;
                            afterLast = 1;
                        }
                        else
                        {
                            first = boundaries.Find(access.BufferRange.Offset);
                            afterLast = boundaries.Find(accessEnd);
                        }
                        if (first < 0 || afterLast < 0)
                            throw new InvalidOperationException(
                                "Normalized buffer access boundaries are missing from dependency tracking.");
                        for (int segment = first; segment < afterLast; segment++)
                        {
                            ref AccessHistory history = ref historyRows[
                                historyOffsetRows[resource] + segment];
                            AddHazardDependencyBits(
                                ref history,
                                access.Flags,
                                queue,
                                includeCrossQueueReads: false,
                                pass,
                                liveFlags,
                                ref dependencyTable);
                        }
                    }
                    queueHistory.Set(queue, pass);

                    ref readonly PassData passRow = ref (
                        canonicalPassRows is not null
                            ? ref canonicalPassRows[pass]
                            : ref graph.Passes[pass]);
                    GraphResourceUsage desired =
                        access.State;
                    MemoryType bufferMemoryType =
                        canonicalBufferRows is not null
                            ? canonicalBufferRows[buffer].MemoryType
                            : graph.Buffers[buffer].MemoryType;
                    if ((bufferMemoryType == MemoryType.Upload &&
                         desired is not (
                             GraphResourceUsage.CopySource or
                             GraphResourceUsage.ShaderResource or
                             GraphResourceUsage.VertexOrConstantBuffer or
                             GraphResourceUsage.IndexBuffer or
                             GraphResourceUsage.IndirectArgument)) ||
                        (bufferMemoryType == MemoryType.Readback &&
                         desired != GraphResourceUsage.CopyDestination))
                    {
                        throw new InvalidOperationException(
                            $"Pass '{graph.GetPassName(pass)}' requests {desired} for " +
                            $"fixed-state {bufferMemoryType} buffer {buffer} " +
                            $"('{graph.GetBufferDescription(buffer).Label ?? "<unnamed>"}').");
                    }
                    if (bufferMemoryType is MemoryType.Upload or MemoryType.Readback)
                    {
                        bufferStateRows[buffer] = desired;
                        bufferLastPassRows[buffer] = pass;
                        bufferLastEffectRows[buffer] = access.Flags;
                        continue;
                    }
                    bool firstTransientAccess =
                        bufferLastPassRows[buffer] < 0 &&
                        !bufferImported;
                    bool transfersQueue =
                        bufferLastPassRows[buffer] >= 0 &&
                        queueRows[bufferLastPassRows[buffer]] != queue;
                    long order = GetBarrierOrder(accessOrdinal, 0);
                    if (firstTransientAccess || transfersQueue ||
                        bufferStateRows[buffer] != desired)
                    {
                        PlannedBarrier transition = PlannedBarrier.BufferTransition(
                            resource,
                            bufferStateRows[buffer],
                            desired,
                            firstTransientAccess);
                        if (transfersQueue || QueueSupportsBarrier(queue, transition.Before, transition.After))
                        {
                            AddIndexedTransition(
                                transition,
                                pass,
                                bufferLastPassRows[buffer],
                                order,
                                queue,
                                bufferLastPassRows[buffer] >= 0
                                    ? queueRows[bufferLastPassRows[buffer]]
                                    : default,
                                ref beforeTable,
                                ref afterTable);
                        }
                        else
                        {
                            commandUnitBarriers.Add(pass, transition, order);
                            if (bufferLastPassRows[buffer] >= 0)
                            {
                                commandUnitBarrierPredecessors.Add(
                                    pass,
                                    bufferLastPassRows[buffer]);
                            }
                        }
                        bufferStateRows[buffer] = desired;
                    }
                    else if (desired == GraphResourceUsage.UnorderedAccess &&
                             bufferLastPassRows[buffer] >= 0 &&
                             (bufferLastEffectRows[buffer] !=
                                  GraphAccess.Read ||
                              access.Flags != GraphAccess.Read))
                    {
                        beforeTable.Add(
                            pass,
                            PlannedBarrier.BufferUnorderedAccess(resource),
                            order);
                    }
                    bufferLastPassRows[buffer] = pass;
                    bufferLastEffectRows[buffer] = access.Flags;
                }

                if (bufferImported)
                {
                    GraphResourceUsage final = FinalState(graph, resource);
                    if (bufferLastPassRows[buffer] >= 0 &&
                        bufferStateRows[buffer] != final)
                    {
                        PlannedBarrier transition = PlannedBarrier.BufferTransition(
                            resource,
                            bufferStateRows[buffer],
                            final);
                        int lastPass = bufferLastPassRows[buffer];
                        long order = finalBarrierOrder++;
                        if (QueueSupportsBarrier(
                                queueRows[lastPass],
                                transition.Before,
                                transition.After))
                        {
                            afterTable.Add(lastPass, transition, order);
                        }
                        else
                        {
                            commandUnitBarriers.Add(
                                finalBarrierKey,
                                transition,
                                order);
                            AddIndexedResourceUsePasses(
                                accessPassRows,
                                resourcePassInputs,
                                liveFlags,
                                firstResourceAccess,
                                afterLastResourceAccess,
                                finalBarrierKey,
                                ref commandUnitBarrierPredecessors);
                        }
                    }
                    bufferStateRows[buffer] = final;
                }
                continue;
            }

            int texture = graph.GetTextureOrdinal(resource);
            bool textureImported = canonicalTextureRows is not null
                ? canonicalTextureRows[texture].IsImported
                : graph.Textures[texture].IsImported;
            ref TextureBarrierTracker tracker =
                ref textureTrackerRows[texture];
            GraphResourceUsage* textureStateRows =
                tracker.States.DangerousPointer;
            int* textureLastPassRows =
                tracker.LastPass.DangerousPointer;
            GraphAccess* textureLastEffectRows =
                tracker.LastEffect.DangerousPointer;
            GraphTextureDescription textureDescription = graph.GetTextureDescription(texture);
            for (int index = firstResourceAccess;
                 index < afterLastResourceAccess;
                 index++)
            {
                int accessOrdinal = resourcePassInputs[index];
                int pass = accessPassRows[accessOrdinal];
                if ((liveFlags[pass] & RenderGraph.PassLiveFlag) == 0)
                    continue;
                ref readonly PassInputData access = ref (
                    canonicalPassInputs is not null
                        ? ref canonicalPassInputs[accessOrdinal]
                        : ref graph.PassInputs[accessOrdinal]);
                if (access.IsBuffer || access.Texture != texture)
                {
                    throw new InvalidOperationException(
                        $"The resource access index mapped canonical access {accessOrdinal} " +
                        $"to texture {texture}, but the access targets " +
                        $"{(access.IsBuffer ? $"buffer {access.Buffer}" : $"texture {access.Texture}")}.");
                }
                QueueType queue = queueRows[pass];
                ref readonly PassData passRow = ref (
                    canonicalPassRows is not null
                        ? ref canonicalPassRows[pass]
                        : ref graph.Passes[pass]);
                GraphResourceUsage desired =
                    DesiredTextureState(graph, passRow, access);
                bool requiresUavOrdering = false;
                int suborder = 0;
                foreach (TextureCell cell in EnumerateCells(access.TextureRange))
                {
                    int cellIndex = cell.Index(textureDescription);
                    ref AccessHistory history = ref historyRows[
                        historyOffsetRows[resource] + cellIndex];
                    AddHazardDependencyBits(
                        ref history,
                        access.Flags,
                        queue,
                        includeCrossQueueReads: true,
                        pass,
                        liveFlags,
                        ref dependencyTable);
                    GraphResourceUsage previous =
                        textureStateRows[cellIndex];
                    bool firstTransientAccess =
                        textureLastPassRows[cellIndex] < 0 &&
                        !textureImported;
                    bool transfersQueue =
                        textureLastPassRows[cellIndex] >= 0 &&
                        queueRows[textureLastPassRows[cellIndex]] != queue;
                    long order = GetBarrierOrder(accessOrdinal, suborder++);
                    if (firstTransientAccess || transfersQueue || previous != desired)
                    {
                        PlannedBarrier transition = PlannedBarrier.TextureTransition(
                            resource,
                            previous,
                            desired,
                            cell.Range,
                            firstTransientAccess);
                        if (transfersQueue || QueueSupportsBarrier(queue, transition.Before, transition.After))
                        {
                            AddIndexedTransition(
                                transition,
                                pass,
                                textureLastPassRows[cellIndex],
                                order,
                                queue,
                                textureLastPassRows[cellIndex] >= 0
                                    ? queueRows[
                                        textureLastPassRows[cellIndex]]
                                    : default,
                                ref beforeTable,
                                ref afterTable);
                        }
                        else
                        {
                            commandUnitBarriers.Add(pass, transition, order);
                            if (textureLastPassRows[cellIndex] >= 0)
                            {
                                commandUnitBarrierPredecessors.Add(
                                    pass,
                                    textureLastPassRows[cellIndex]);
                            }
                        }
                        textureStateRows[cellIndex] = desired;
                    }
                    else if (desired == GraphResourceUsage.UnorderedAccess &&
                             textureLastPassRows[cellIndex] >= 0 &&
                             (textureLastEffectRows[cellIndex] !=
                                  GraphAccess.Read ||
                              access.Flags != GraphAccess.Read))
                    {
                        requiresUavOrdering = true;
                    }
                    textureLastPassRows[cellIndex] = pass;
                    textureLastEffectRows[cellIndex] = access.Flags;
                }
                if (requiresUavOrdering)
                {
                    beforeTable.Add(
                        pass,
                        PlannedBarrier.TextureUnorderedAccess(
                            resource,
                            access.TextureRange),
                        GetBarrierOrder(accessOrdinal, suborder));
                }
            }

            if (!textureImported) continue;
            bool used = false;
            for (int cellIndex = 0;
                 cellIndex < tracker.LastPass.Length;
                 cellIndex++)
            {
                int lastPass = textureLastPassRows[cellIndex];
                if (lastPass < 0) continue;
                used = true;
                break;
            }
            if (!used) continue;
            GraphResourceUsage textureFinal = FinalState(graph, resource);
            TextureSubresourceRange whole = new(
                0,
                checked((uint)textureDescription.MipLevels),
                0,
                checked((uint)textureDescription.ArrayLayers),
                PlanesFor(textureDescription.Format));
            bool addedCommandUnitBarrier = false;
            foreach (TextureCell cell in EnumerateCells(whole))
            {
                int cellIndex = cell.Index(textureDescription);
                if (textureStateRows[cellIndex] == textureFinal) continue;
                PlannedBarrier transition = PlannedBarrier.TextureTransition(
                    resource,
                    textureStateRows[cellIndex],
                    textureFinal,
                    cell.Range);
                int lastPass = textureLastPassRows[cellIndex];
                long order = finalBarrierOrder++;
                if (lastPass >= 0 &&
                    QueueSupportsBarrier(
                        queueRows[lastPass],
                        transition.Before,
                        transition.After))
                {
                    afterTable.Add(lastPass, transition, order);
                }
                else
                {
                    commandUnitBarriers.Add(
                        finalBarrierKey,
                        transition,
                        order);
                    addedCommandUnitBarrier = true;
                }
            }
            if (addedCommandUnitBarrier)
            {
                AddIndexedResourceUsePasses(
                    accessPassRows,
                    resourcePassInputs,
                    liveFlags,
                    firstResourceAccess,
                    afterLastResourceAccess,
                    finalBarrierKey,
                    ref commandUnitBarrierPredecessors);
            }
            new Span<GraphResourceUsage>(
                textureStateRows,
                tracker.States.Length).Fill(textureFinal);
        }

        dependencyTable.WriteToDependencies(graph, liveOnly: true);
        beforeTable.WriteTo(graph, before: true);
        afterTable.WriteTo(graph, before: false);
        ValidateFixedStateBufferBarriers(graph);

        graph.BufferFinalStates = bufferStates;
        graph.TextureFinalStateOffsets =
            graph.AllocateSlice<int>(textureCount + 1, clear: false);
        int textureStateCount = 0;
        Span<int> textureFinalOffsets =
            graph.TextureFinalStateOffsets.Span;
        for (int texture = 0; texture < textureCount; texture++)
        {
            textureFinalOffsets[texture] = textureStateCount;
            textureStateCount = checked(
                textureStateCount +
                textureTrackerRows[texture].States.Length);
        }
        textureFinalOffsets[textureCount] = textureStateCount;
        graph.TextureFinalStates =
            graph.AllocateSlice<GraphResourceUsage>(textureStateCount, clear: false);
        Span<GraphResourceUsage> textureFinalStates =
            graph.TextureFinalStates.Span;
        for (int texture = 0; texture < textureCount; texture++)
        {
            int offset = textureFinalOffsets[texture];
            textureTrackerRows[texture].States.ReadOnlySpan.CopyTo(
                textureFinalStates.Slice(
                    offset,
                    textureTrackerRows[texture].States.Length));
        }
    }

    private static long GetBarrierOrder(int accessOrdinal, int suborder) =>
        checked(((long)accessOrdinal << 32) | (uint)suborder);

    private static void ValidateFixedStateBufferBarriers(RenderGraph graph)
    {
        for (int pass = 0; pass < graph.Passes.Length; pass++)
        {
            Validate(graph.GetBeforeBarriers(pass), pass, "before");
            Validate(graph.GetAfterBarriers(pass), pass, "after");
        }

        void Validate(
            ReadOnlySpan<PlannedBarrier> barriers,
            int pass,
            string placement)
        {
            foreach (ref readonly PlannedBarrier barrier in barriers)
            {
                if (barrier.IsTransition ||
                    !graph.IsBufferResourceOrdinal(barrier.Resource))
                {
                    continue;
                }
                int buffer = graph.GetBufferOrdinal(barrier.Resource);
                MemoryType memoryType = graph.Buffers[buffer].MemoryType;
                if (memoryType is not (MemoryType.Upload or MemoryType.Readback))
                    continue;
                throw new InvalidOperationException(
                    $"Pass '{graph.GetPassName(pass)}' has a {placement} unordered-access " +
                    $"barrier for fixed-state {memoryType} buffer {buffer} " +
                    $"('{graph.GetBufferDescription(buffer).Label ?? "<unnamed>"}').");
            }
        }
    }

    private static void AddIndexedTransition(
        in PlannedBarrier transition,
        int pass,
        int lastPass,
        long order,
        QueueType queue,
        QueueType previousQueue,
        ref PassBarrierTable before,
        ref PassBarrierTable after)
    {
        if (lastPass >= 0 && previousQueue != queue)
        {
            if (transition.UsesPlacementInitialState)
                throw new InvalidOperationException("A first-use placement transition cannot transfer queue ownership.");
            after.Add(lastPass, transition.AsQueueRelease(queue), order);
            before.Add(pass, transition.AsQueueAcquire(previousQueue), order);
            return;
        }
        before.Add(pass, transition, order);
    }

    private static unsafe void AddIndexedResourceUsePasses(
        int* accessPassOrdinals,
        int* resourceAccessOrdinals,
        byte* liveFlags,
        int first,
        int afterLast,
        int successor,
        ref PassPredecessorTable predecessors)
    {
        for (int index = first; index < afterLast; index++)
        {
            int pass = accessPassOrdinals[resourceAccessOrdinals[index]];
            if ((liveFlags[pass] & RenderGraph.PassLiveFlag) != 0)
                predecessors.Add(successor, pass);
        }
    }

    private static unsafe void AddCrossQueueDependencyBits(
        in ResourceQueueHistory history,
        QueueType queue,
        int current,
        byte* liveFlags,
        ref PassPredecessorTable dependencies)
    {
        if (queue != QueueType.Graphics)
            AddIndexedDependency(
                history.Graphics,
                current,
                liveFlags,
                ref dependencies);
        if (queue != QueueType.Compute)
            AddIndexedDependency(
                history.Compute,
                current,
                liveFlags,
                ref dependencies);
        if (queue != QueueType.Copy)
            AddIndexedDependency(
                history.Copy,
                current,
                liveFlags,
                ref dependencies);
    }

    private static unsafe void AddHazardDependencyBits(
        ref AccessHistory history,
        GraphAccess effect,
        QueueType queue,
        bool includeCrossQueueReads,
        int current,
        byte* liveFlags,
        ref PassPredecessorTable dependencies)
    {
        AddIndexedDependency(
            history.LastWriter,
            current,
            liveFlags,
            ref dependencies);
        if (effect != GraphAccess.Read)
        {
            AddIndexedDependency(
                history.GraphicsReader,
                current,
                liveFlags,
                ref dependencies);
            AddIndexedDependency(
                history.ComputeReader,
                current,
                liveFlags,
                ref dependencies);
            AddIndexedDependency(
                history.CopyReader,
                current,
                liveFlags,
                ref dependencies);
        }
        if (includeCrossQueueReads)
        {
            if (queue != QueueType.Graphics)
            {
                AddIndexedDependency(
                    history.GraphicsAccess,
                    current,
                    liveFlags,
                    ref dependencies);
            }
            if (queue != QueueType.Compute)
            {
                AddIndexedDependency(
                    history.ComputeAccess,
                    current,
                    liveFlags,
                    ref dependencies);
            }
            if (queue != QueueType.Copy)
            {
                AddIndexedDependency(
                    history.CopyAccess,
                    current,
                    liveFlags,
                    ref dependencies);
            }
        }

        if (effect == GraphAccess.Read)
        {
            history.SetReader(queue, current);
        }
        else
        {
            history.LastWriter = current;
            history.GraphicsReader = -1;
            history.ComputeReader = -1;
            history.CopyReader = -1;
        }
        history.SetAccess(queue, current);
    }

    private static unsafe void AddIndexedDependency(
        int dependency,
        int current,
        byte* liveFlags,
        ref PassPredecessorTable dependencies)
    {
        if (dependency < 0 ||
            dependency == current ||
            (liveFlags[dependency] & RenderGraph.PassLiveFlag) == 0)
        {
            return;
        }
        dependencies.Add(current, dependency);
    }

    internal static void BuildDependenciesReference(
        RenderGraph graph,
        ArenaSlice<QueueType> queues)
    {
        graph.DependencyRows.Clear();
        int passCount = graph.Passes.Length;
        int resourceCount = graph.ResourceCount;
        ArenaSlice<BufferBoundaryIndex> bufferBoundaries = graph.BufferBoundaries;
        ArenaSlice<int> historyOffsets =
            graph.AllocateSlice<int>(checked(resourceCount + 1), clear: false);
        int historyCount = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            historyOffsets[resource] = historyCount;
            if (!graph.IsResourceLive(resource)) continue;
            if (graph.IsBufferResourceOrdinal(resource))
            {
                int segmentCount = Math.Max(0, bufferBoundaries[graph.GetBufferOrdinal(resource)].Count - 1);
                historyCount = checked(historyCount + segmentCount);
            }
            else
            {
                GraphTextureDescription texture = graph.GetTextureDescription(graph.GetTextureOrdinal(resource));
                int planes = GraphFormat.HasStencil(texture.Format) ? 2 : 1;
                int cellCount = checked(texture.MipLevels * texture.ArrayLayers * planes);
                historyCount = checked(historyCount + cellCount);
            }
        }
        historyOffsets[resourceCount] = historyCount;
        ArenaSlice<AccessHistory> accessHistories =
            graph.AllocateSlice<AccessHistory>(historyCount, clear: false);
        accessHistories.Span.Fill(AccessHistory.Empty);
        ArenaSlice<ResourceQueueHistory> bufferQueues =
            graph.AllocateSlice<ResourceQueueHistory>(graph.Buffers.Length, clear: false);
        bufferQueues.Span.Fill(ResourceQueueHistory.Empty);

        ArenaSlice<int> dependencyMarks = graph.AllocateSlice<int>(passCount);
        ArenaSlice<int> dependencies = graph.AllocateSlice<int>(passCount, clear: false);
        for (int current = 0; current < passCount; current++)
        {
            if (!graph.IsPassLive(current))
            {
                graph.Passes[current].DependencyOffset = graph.DependencyRows.Count;
                graph.Passes[current].DependencyCount = 0;
                continue;
            }

            int dependencyCount = 0;
            int stamp = checked(current + 1);
            QueueType queue = queues[current];
            foreach (ref readonly PassInputData access in graph.GetPassAccesses(graph.Passes[current]))
            {
                int resource = graph.GetResourceOrdinal(access);
                if (!graph.IsResourceLive(resource)) continue;
                if (access.IsBuffer)
                {
                    ref ResourceQueueHistory resourceQueue = ref bufferQueues[access.Buffer];
                    AddCrossQueueDependencies(
                        resourceQueue,
                        queue,
                        current,
                        stamp,
                        graph,
                        dependencyMarks,
                        dependencies,
                        ref dependencyCount);

                    if (graph.IsResourceWritten(resource))
                    {
                        BufferBoundaryIndex boundaries = bufferBoundaries[access.Buffer];
                        ulong accessEnd = checked(access.BufferRange.Offset + access.BufferRange.Size);
                        int first;
                        int afterLast;
                        if (boundaries.Count == 2 &&
                            boundaries[0] == access.BufferRange.Offset &&
                            boundaries[1] == accessEnd)
                        {
                            first = 0;
                            afterLast = 1;
                        }
                        else
                        {
                            first = boundaries.Find(access.BufferRange.Offset);
                            afterLast = boundaries.Find(accessEnd);
                        }
                        if (first < 0 || afterLast < 0)
                            throw new InvalidOperationException("Normalized buffer access boundaries are missing from dependency tracking.");
                        for (int segment = first; segment < afterLast; segment++)
                        {
                            ref AccessHistory history =
                                ref accessHistories[historyOffsets[resource] + segment];
                            AddHazardDependencies(
                                ref history,
                                access.Flags,
                                queue,
                                includeCrossQueueReads: false,
                                current,
                                stamp,
                                graph,
                                dependencyMarks,
                                dependencies,
                                ref dependencyCount);
                        }
                    }
                    resourceQueue.Set(queue, current);
                    continue;
                }

                TextureSubresourceRange range = access.TextureRange;
                for (int layer = checked((int)range.FirstArrayLayer); layer < checked((int)(range.FirstArrayLayer + range.ArrayLayerCount)); layer++)
                for (int mip = checked((int)range.FirstMipLevel); mip < checked((int)(range.FirstMipLevel + range.MipLevelCount)); mip++)
                {
                    GraphTextureDescription textureDescription = graph.GetTextureDescription(access.Texture);
                    int index = checked(mip + layer * textureDescription.MipLevels);
                    if ((range.Aspects & (TextureAspects.Color | TextureAspects.Depth)) != 0)
                    {
                        ref AccessHistory history =
                            ref accessHistories[historyOffsets[resource] + index];
                        AddHazardDependencies(
                            ref history,
                            access.Flags,
                            queue,
                            includeCrossQueueReads: true,
                            current,
                            stamp,
                            graph,
                            dependencyMarks,
                            dependencies,
                            ref dependencyCount);
                    }
                    if ((range.Aspects & TextureAspects.Stencil) != 0)
                    {
                        int stencilIndex = checked(
                            index + textureDescription.MipLevels * textureDescription.ArrayLayers);
                        ref AccessHistory history =
                            ref accessHistories[historyOffsets[resource] + stencilIndex];
                        AddHazardDependencies(
                            ref history,
                            access.Flags,
                            queue,
                            includeCrossQueueReads: true,
                            current,
                            stamp,
                            graph,
                            dependencyMarks,
                            dependencies,
                            ref dependencyCount);
                    }
                }
            }

            Span<int> passDependencies = dependencies.Span[..dependencyCount];
            passDependencies.Sort();
            graph.Passes[current].DependencyOffset = graph.DependencyRows.Count;
            graph.Passes[current].DependencyCount = dependencyCount;
            foreach (int dependency in passDependencies) graph.DependencyRows.Add(dependency);
        }
    }

    private static void AddHazardDependencies(
        ref AccessHistory history,
        GraphAccess effect,
        QueueType queue,
        bool includeCrossQueueReads,
        int current,
        int stamp,
        RenderGraph graph,
        ArenaSlice<int> dependencyMarks,
        ArenaSlice<int> dependencies,
        ref int dependencyCount)
    {
        AddDependency(history.LastWriter, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
        if (effect != GraphAccess.Read)
        {
            AddDependency(history.GraphicsReader, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
            AddDependency(history.ComputeReader, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
            AddDependency(history.CopyReader, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
        }
        if (includeCrossQueueReads)
        {
            if (queue != QueueType.Graphics)
                AddDependency(history.GraphicsAccess, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
            if (queue != QueueType.Compute)
                AddDependency(history.ComputeAccess, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
            if (queue != QueueType.Copy)
                AddDependency(history.CopyAccess, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
        }

        if (effect == GraphAccess.Read)
        {
            history.SetReader(queue, current);
        }
        else
        {
            history.LastWriter = current;
            history.GraphicsReader = -1;
            history.ComputeReader = -1;
            history.CopyReader = -1;
        }
        history.SetAccess(queue, current);
    }

    private static void AddCrossQueueDependencies(
        in ResourceQueueHistory history,
        QueueType queue,
        int current,
        int stamp,
        RenderGraph graph,
        ArenaSlice<int> dependencyMarks,
        ArenaSlice<int> dependencies,
        ref int dependencyCount)
    {
        if (queue != QueueType.Graphics)
            AddDependency(history.Graphics, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
        if (queue != QueueType.Compute)
            AddDependency(history.Compute, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
        if (queue != QueueType.Copy)
            AddDependency(history.Copy, current, stamp, graph, dependencyMarks, dependencies, ref dependencyCount);
    }

    private static void AddDependency(
        int dependency,
        int current,
        int stamp,
        RenderGraph graph,
        ArenaSlice<int> dependencyMarks,
        ArenaSlice<int> dependencies,
        ref int dependencyCount)
    {
        if (dependency < 0 || dependency == current || !graph.IsPassLive(dependency) || dependencyMarks[dependency] == stamp)
            return;
        dependencyMarks[dependency] = stamp;
        dependencies[dependencyCount++] = dependency;
    }

    private struct ResourceQueueHistory
    {
        internal static readonly ResourceQueueHistory Empty = new()
        {
            Graphics = -1,
            Compute = -1,
            Copy = -1,
        };

        public int Graphics;
        public int Compute;
        public int Copy;

        public void Set(QueueType queue, int pass)
        {
            switch (queue)
            {
                case QueueType.Graphics: Graphics = pass; break;
                case QueueType.Compute: Compute = pass; break;
                case QueueType.Copy: Copy = pass; break;
                default: throw new ArgumentOutOfRangeException(nameof(queue));
            }
        }
    }

    private struct AccessHistory
    {
        internal static readonly AccessHistory Empty = new()
        {
            LastWriter = -1,
            GraphicsReader = -1,
            ComputeReader = -1,
            CopyReader = -1,
            GraphicsAccess = -1,
            ComputeAccess = -1,
            CopyAccess = -1,
        };

        public int LastWriter;
        public int GraphicsReader;
        public int ComputeReader;
        public int CopyReader;
        public int GraphicsAccess;
        public int ComputeAccess;
        public int CopyAccess;

        public void SetReader(QueueType queue, int pass)
        {
            switch (queue)
            {
                case QueueType.Graphics: GraphicsReader = pass; break;
                case QueueType.Compute: ComputeReader = pass; break;
                case QueueType.Copy: CopyReader = pass; break;
                default: throw new ArgumentOutOfRangeException(nameof(queue));
            }
        }

        public void SetAccess(QueueType queue, int pass)
        {
            switch (queue)
            {
                case QueueType.Graphics: GraphicsAccess = pass; break;
                case QueueType.Compute: ComputeAccess = pass; break;
                case QueueType.Copy: CopyAccess = pass; break;
                default: throw new ArgumentOutOfRangeException(nameof(queue));
            }
        }
    }

    private static void BuildBarriersReference(
        RenderGraph graph,
        Device device,
        ArenaSlice<QueueType> queues,
        out PassBarrierTable commandUnitBarriers,
        out PassPredecessorTable commandUnitBarrierPredecessors)
    {
        int passCount = graph.Passes.Length;
        PassBarrierTable afterTable = new(graph, passCount);
        commandUnitBarriers = new PassBarrierTable(graph, checked(passCount + 1));
        commandUnitBarrierPredecessors = new PassPredecessorTable(
            graph,
            checked(passCount + 1));
        int finalBarrierKey = passCount;
        int bufferCount = graph.Buffers.Length;
        int textureCount = graph.Textures.Length;
        ArenaSlice<GraphResourceUsage> bufferStates =
            graph.AllocateSlice<GraphResourceUsage>(bufferCount, clear: false);
        ArenaSlice<int> bufferLastPass = graph.AllocateSlice<int>(bufferCount, clear: false);
        bufferLastPass.Span.Fill(-1);
        ArenaSlice<GraphAccess> bufferLastEffect =
            graph.AllocateSlice<GraphAccess>(bufferCount, clear: false);
        ArenaSlice<TextureBarrierTracker> textureTrackers =
            graph.AllocateSlice<TextureBarrierTracker>(textureCount, clear: false);

        for (int buffer = 0; buffer < bufferCount; buffer++)
        {
            int resource = graph.GetBufferResourceOrdinal(buffer);
            bufferStates[buffer] = InitialState(graph, resource);
        }
        for (int texture = 0; texture < textureCount; texture++)
        {
            int resource = graph.GetTextureResourceOrdinal(texture);
            textureTrackers[texture] = new TextureBarrierTracker(
                graph,
                graph.GetTextureDescription(texture),
                InitialState(graph, resource));
        }

        for (int pass = 0; pass < passCount; pass++)
        {
            ref PassData passCompilation = ref graph.Passes[pass];
            passCompilation.BeforeBarrierOffset = graph.BeforeResourceBarriers.Count;
            passCompilation.BeforeBarrierCount = 0;
            if (!graph.IsPassLive(pass)) continue;
            ref readonly PassData passRow = ref graph.Passes[pass];
            foreach (ref readonly PassInputData access in graph.GetPassAccesses(passRow))
            {
                GraphResourceUsage desired = DesiredState(graph, passRow, access);
                if (access.IsBuffer)
                {
                    int resource = graph.GetResourceOrdinal(access);
                    int buffer = access.Buffer;
                    bool firstTransientAccess = bufferLastPass[buffer] < 0 && !graph.Buffers[buffer].IsImported;
                    bool transfersQueue = bufferLastPass[buffer] >= 0 &&
                        queues[bufferLastPass[buffer]] != queues[pass];
                    if (firstTransientAccess || transfersQueue || bufferStates[buffer] != desired)
                    {
                        PlannedBarrier transition = PlannedBarrier.BufferTransition(
                            resource,
                            bufferStates[buffer],
                            desired,
                            firstTransientAccess);
                        if (transfersQueue || QueueSupportsBarrier(queues[pass], transition.Before, transition.After))
                        {
                            AddTransition(
                                transition,
                                pass,
                                bufferLastPass[buffer],
                                queues,
                                graph,
                                ref afterTable);
                        }
                        else
                        {
                            commandUnitBarriers.Add(pass, transition);
                            if (bufferLastPass[buffer] >= 0)
                                commandUnitBarrierPredecessors.Add(pass, bufferLastPass[buffer]);
                        }
                        bufferStates[buffer] = desired;
                    }
                    else if (desired == GraphResourceUsage.UnorderedAccess && bufferLastPass[buffer] >= 0 &&
                             (bufferLastEffect[buffer] != GraphAccess.Read || access.Flags != GraphAccess.Read))
                    {
                        AddBeforeBarrier(
                            graph,
                            pass,
                            PlannedBarrier.BufferUnorderedAccess(resource));
                    }
                    bufferLastPass[buffer] = pass;
                    bufferLastEffect[buffer] = access.Flags;
                    continue;
                }

                int textureResource = graph.GetResourceOrdinal(access);
                ref TextureBarrierTracker tracker = ref textureTrackers[access.Texture];
                bool requiresUavOrdering = false;
                foreach (TextureCell cell in EnumerateCells(access.TextureRange))
                {
                    int index = cell.Index(graph.GetTextureDescription(access.Texture));
                    GraphResourceUsage previous = tracker.States[index];
                    bool firstTransientAccess = tracker.LastPass[index] < 0 &&
                        !graph.Textures[access.Texture].IsImported;
                    bool transfersQueue = tracker.LastPass[index] >= 0 &&
                        queues[tracker.LastPass[index]] != queues[pass];
                    if (firstTransientAccess || transfersQueue || previous != desired)
                    {
                        PlannedBarrier transition = PlannedBarrier.TextureTransition(
                            textureResource,
                            previous,
                            desired,
                            cell.Range,
                            firstTransientAccess);
                        if (transfersQueue || QueueSupportsBarrier(queues[pass], transition.Before, transition.After))
                        {
                            AddTransition(
                                transition,
                                pass,
                                tracker.LastPass[index],
                                queues,
                                graph,
                                ref afterTable);
                        }
                        else
                        {
                            commandUnitBarriers.Add(pass, transition);
                            if (tracker.LastPass[index] >= 0)
                                commandUnitBarrierPredecessors.Add(pass, tracker.LastPass[index]);
                        }
                        tracker.States[index] = desired;
                    }
                    else if (desired == GraphResourceUsage.UnorderedAccess && tracker.LastPass[index] >= 0 &&
                             (tracker.LastEffect[index] != GraphAccess.Read || access.Flags != GraphAccess.Read))
                    {
                        requiresUavOrdering = true;
                    }
                    tracker.LastPass[index] = pass;
                    tracker.LastEffect[index] = access.Flags;
                }
                if (requiresUavOrdering)
                    AddBeforeBarrier(
                        graph,
                        pass,
                        PlannedBarrier.TextureUnorderedAccess(
                            textureResource,
                            access.TextureRange));
            }
        }

        for (int resource = 0; resource < graph.ResourceCount; resource++)
        {
            if (!graph.IsResourceImported(resource)) continue;
            GraphResourceUsage final = FinalState(graph, resource);
            if (graph.IsBufferResourceOrdinal(resource))
            {
                int buffer = graph.GetBufferOrdinal(resource);
                if (bufferLastPass[buffer] >= 0 && bufferStates[buffer] != final)
                {
                    PlannedBarrier transition = PlannedBarrier.BufferTransition(
                        resource,
                        bufferStates[buffer],
                        final);
                    int lastPass = bufferLastPass[buffer];
                    if (QueueSupportsBarrier(queues[lastPass], transition.Before, transition.After))
                    {
                        afterTable.Add(lastPass, transition);
                    }
                    else
                    {
                        commandUnitBarriers.Add(finalBarrierKey, transition);
                        AddResourceUsePasses(
                            graph,
                            resource,
                            finalBarrierKey,
                            ref commandUnitBarrierPredecessors);
                    }
                }
                bufferStates[buffer] = final;
                continue;
            }

            int texture = graph.GetTextureOrdinal(resource);
            ref TextureBarrierTracker tracker = ref textureTrackers[texture];
            bool used = false;
            foreach (int lastPass in tracker.LastPass)
            {
                if (lastPass < 0) continue;
                used = true;
                break;
            }
            if (!used) continue;
            TextureSubresourceRange whole = new(
                0,
                checked((uint)graph.GetTextureDescription(texture).MipLevels),
                0,
                checked((uint)graph.GetTextureDescription(texture).ArrayLayers),
                PlanesFor(graph.GetTextureDescription(texture).Format));
            bool addedCommandUnitBarrier = false;
            foreach (TextureCell cell in EnumerateCells(whole))
            {
                int index = cell.Index(graph.GetTextureDescription(texture));
                if (tracker.States[index] == final) continue;
                PlannedBarrier transition = PlannedBarrier.TextureTransition(
                    resource,
                    tracker.States[index],
                    final,
                    cell.Range);
                int lastPass = tracker.LastPass[index];
                if (lastPass >= 0 && QueueSupportsBarrier(queues[lastPass], transition.Before, transition.After))
                {
                    afterTable.Add(lastPass, transition);
                }
                else
                {
                    commandUnitBarriers.Add(finalBarrierKey, transition);
                    addedCommandUnitBarrier = true;
                }
            }
            if (addedCommandUnitBarrier)
                AddResourceUsePasses(
                    graph,
                    resource,
                    finalBarrierKey,
                    ref commandUnitBarrierPredecessors);
            if (used) tracker.States.Span.Fill(final);
        }

        graph.BufferFinalStates = bufferStates;
        graph.TextureFinalStateOffsets = graph.AllocateSlice<int>(textureCount + 1, clear: false);
        int textureStateCount = 0;
        for (int texture = 0; texture < textureCount; texture++)
        {
            graph.TextureFinalStateOffsets[texture] = textureStateCount;
            textureStateCount = checked(textureStateCount + textureTrackers[texture].States.Length);
        }
        graph.TextureFinalStateOffsets[textureCount] = textureStateCount;
        graph.TextureFinalStates = graph.AllocateSlice<GraphResourceUsage>(textureStateCount, clear: false);
        for (int texture = 0; texture < textureCount; texture++)
        {
            int offset = graph.TextureFinalStateOffsets[texture];
            textureTrackers[texture].States.ReadOnlySpan.CopyTo(
                graph.TextureFinalStates.Span.Slice(offset, textureTrackers[texture].States.Length));
        }

        afterTable.WriteTo(graph, before: false);
    }

    private static void AddResourceUsePasses(
        RenderGraph graph,
        int resource,
        int successor,
        ref PassPredecessorTable predecessors)
    {
        foreach (int pass in graph.ActivePassOrdinals)
        {
            foreach (ref readonly PassInputData access in graph.GetPassAccesses(graph.Passes[pass]))
            {
                if (graph.GetResourceOrdinal(access) != resource) continue;
                predecessors.Add(successor, pass);
                break;
            }
        }
    }

    private static void AddTransition(
        in PlannedBarrier transition,
        int pass,
        int lastPass,
        ArenaSlice<QueueType> queues,
        RenderGraph graph,
        ref PassBarrierTable after)
    {
        if (lastPass >= 0 && queues[lastPass] != queues[pass])
        {
            if (transition.UsesPlacementInitialState)
                throw new InvalidOperationException("A first-use placement transition cannot transfer queue ownership.");
            after.Add(lastPass, transition.AsQueueRelease(queues[pass]));
            AddBeforeBarrier(graph, pass, transition.AsQueueAcquire(queues[lastPass]));
            return;
        }
        AddBeforeBarrier(graph, pass, transition);
    }

    private static void AddBeforeBarrier(
        RenderGraph graph,
        int pass,
        in PlannedBarrier barrier)
    {
        ref PassData compilation = ref graph.Passes[pass];
        if (compilation.BeforeBarrierCount == 0)
            compilation.BeforeBarrierOffset = graph.BeforeResourceBarriers.Count;
        else if (compilation.BeforeBarrierOffset + compilation.BeforeBarrierCount != graph.BeforeResourceBarriers.Count)
            throw new InvalidOperationException("Before-barriers must be emitted in pass order.");
        graph.BeforeResourceBarriers.Add(barrier);
        compilation.BeforeBarrierCount++;
    }

    private unsafe struct PassBarrierTable
    {
        private readonly RenderGraph _graph;
        private readonly PassBarrierChain* _chains;
        private readonly int _keyCount;
        private readonly int _entryCapacity;
        private PassBarrierEntry* _entryRows;
        private ArenaColumn<PassBarrierEntry> _entries;

        public PassBarrierTable(
            RenderGraph graph,
            int passCount,
            int entryCapacity = 0)
        {
            _graph = graph;
            ArenaSlice<PassBarrierChain> chainStorage =
                graph.AllocateSlice<PassBarrierChain>(
                    passCount,
                    clear: false);
            _chains = chainStorage.DangerousPointer;
            _keyCount = passCount;
            for (int pass = 0; pass < passCount; pass++)
                _chains[pass] = new PassBarrierChain { Head = -1, Count = 0 };
            _entries =
                graph.CreateArenaColumn<PassBarrierEntry>(entryCapacity);
            _entryCapacity = entryCapacity;
            _entryRows = entryCapacity == 0
                ? null
                : _entries.DangerousContiguousPointer;
        }

        public void Add(int pass, in PlannedBarrier barrier) =>
            Add(pass, barrier, _entries.Count);

        public void Add(int pass, in PlannedBarrier barrier, long order)
        {
            if (!barrier.IsTransition &&
                _graph.IsBufferResourceOrdinal(barrier.Resource))
            {
                int buffer = _graph.GetBufferOrdinal(barrier.Resource);
                MemoryType memoryType = _graph.Buffers[buffer].MemoryType;
                if (memoryType is MemoryType.Upload or MemoryType.Readback)
                {
                    throw new InvalidOperationException(
                        $"Attempted to add an unordered-access barrier for fixed-state " +
                        $"{memoryType} buffer {buffer} to pass '{_graph.GetPassName(Math.Min(pass, _graph.Passes.Length - 1))}'.");
                }
            }
            ref PassBarrierChain chain = ref _chains[pass];
            int entryIndex = _entries.Count;
            if (entryIndex >= _entryCapacity)
                _entryRows = null;
            PassBarrierEntry* entries = _entryRows;
            if (chain.Head < 0)
            {
                _entries.Add(new PassBarrierEntry(barrier, order, chain.Head));
                chain.Head = entryIndex;
                chain.Count++;
                return;
            }
            ref PassBarrierEntry head = ref (
                entries is not null
                    ? ref entries[chain.Head]
                    : ref _entries[chain.Head]);
            if (head.Order <= order)
            {
                if (head.Barrier.Resource == barrier.Resource &&
                    head.Barrier.IsTransition == barrier.IsTransition &&
                    barrier.IsTexture &&
                    TryMergeCompatibleTextureBarriers(
                        head.Barrier,
                        barrier,
                        out PlannedBarrier merged))
                {
                    head.Barrier = merged;
                    return;
                }
                _entries.Add(new PassBarrierEntry(barrier, order, chain.Head));
                chain.Head = entryIndex;
                chain.Count++;
                return;
            }

            int cursor = chain.Head;
            int cursorPrevious = entries is not null
                ? entries[cursor].Previous
                : _entries[cursor].Previous;
            while (cursorPrevious >= 0)
            {
                ref PassBarrierEntry previousEntry = ref (
                    entries is not null
                        ? ref entries[cursorPrevious]
                        : ref _entries[cursorPrevious]);
                if (previousEntry.Order <= order) break;
                cursor = cursorPrevious;
                cursorPrevious = previousEntry.Previous;
            }
            int previous = cursorPrevious;
            if (previous >= 0)
            {
                ref PassBarrierEntry earlier = ref (
                    entries is not null
                        ? ref entries[previous]
                        : ref _entries[previous]);
                if (earlier.Order <= order &&
                    earlier.Barrier.Resource == barrier.Resource &&
                    earlier.Barrier.IsTransition == barrier.IsTransition &&
                    barrier.IsTexture &&
                    TryMergeCompatibleTextureBarriers(
                        earlier.Barrier,
                        barrier,
                        out PlannedBarrier merged))
                {
                    earlier.Barrier = merged;
                    earlier.Order = order;
                    return;
                }
            }
            _entries.Add(new PassBarrierEntry(barrier, order, previous));
            ref PassBarrierEntry cursorEntry = ref (
                entries is not null
                    ? ref entries[cursor]
                    : ref _entries[cursor]);
            cursorEntry.Previous = entryIndex;
            chain.Count++;
        }

        public int GetCount(int pass) => _chains[pass].Count;

        public int KeyCount => _keyCount;

        public int EntryCount => _entries.Count;

        public int NonEmptyKeyCount
        {
            get
            {
                int count = 0;
                for (int pass = 0; pass < _keyCount; pass++)
                    if (_chains[pass].Count != 0) count++;
                return count;
            }
        }

        public void AppendTo(
            ref ArenaColumn<PlannedBarrier> rows,
            int pass,
            out int offset,
            out int count)
        {
            PassBarrierChain chain = _chains[pass];
            offset = rows.Count;
            if (chain.Count == 0)
            {
                count = 0;
                return;
            }
            rows.EnsureAppendCapacity(chain.Count);
            Span<PlannedBarrier> result =
                rows.AddUninitialized(chain.Count);
            int destination = result.Length - 1;
            PassBarrierEntry* entries = _entryRows;
            for (int entryIndex = chain.Head; entryIndex >= 0;)
            {
                PassBarrierEntry entry = entries is not null
                    ? entries[entryIndex]
                    : _entries[entryIndex];
                result[destination--] = entry.Barrier;
                entryIndex = entry.Previous;
            }
            count = chain.Count;
        }

        public void WriteTo(RenderGraph graph, bool before)
        {
            ref ArenaColumn<PlannedBarrier> rows = ref (before
                ? ref graph.BeforeResourceBarriers
                : ref graph.AfterResourceBarriers);
            rows.EnsureCapacity(checked(rows.Count + _entries.Count));
            for (int pass = 0; pass < _keyCount; pass++)
            {
                PassBarrierChain chain = _chains[pass];
                ref PassData passRow = ref graph.Passes[pass];
                int offset = rows.Count;
                if (before)
                {
                    passRow.BeforeBarrierOffset = offset;
                }
                else
                {
                    passRow.AfterBarrierOffset = offset;
                }
                if (chain.Count == 0)
                {
                    if (before)
                        passRow.BeforeBarrierCount = 0;
                    else
                        passRow.AfterBarrierCount = 0;
                    continue;
                }
                rows.EnsureAppendCapacity(chain.Count);
                Span<PlannedBarrier> barriers =
                    rows.AddUninitialized(chain.Count);
                int destination = barriers.Length - 1;
                PassBarrierEntry* entries = _entryRows;
                for (int entryIndex = chain.Head; entryIndex >= 0;)
                {
                    PassBarrierEntry entry = entries is not null
                        ? entries[entryIndex]
                        : _entries[entryIndex];
                    barriers[destination--] = entry.Barrier;
                    entryIndex = entry.Previous;
                }
                if (before)
                    passRow.BeforeBarrierCount = chain.Count;
                else
                    passRow.AfterBarrierCount = chain.Count;
            }
        }

        private static bool TryMergeCompatibleTextureBarriers(
            in PlannedBarrier left,
            in PlannedBarrier right,
            out PlannedBarrier merged)
        {
            merged = default;
            if (left.IsTransition &&
                (left.Before != right.Before ||
                 left.After != right.After ||
                 left.Kind != right.Kind ||
                 left.OtherQueue != right.OtherQueue ||
                 left.UsesPlacementInitialState ||
                 right.UsesPlacementInitialState))
            {
                return false;
            }

            TextureSubresourceRange first = left.TextureRange;
            TextureSubresourceRange second = right.TextureRange;
            if (first.Aspects != second.Aspects) return false;

            TextureSubresourceRange range;
            if (first.FirstArrayLayer == second.FirstArrayLayer &&
                first.ArrayLayerCount == second.ArrayLayerCount &&
                first.FirstMipLevel + first.MipLevelCount == second.FirstMipLevel)
            {
                range = first with
                {
                    MipLevelCount = checked(first.MipLevelCount + second.MipLevelCount),
                };
            }
            else if (first.FirstMipLevel == second.FirstMipLevel &&
                     first.MipLevelCount == second.MipLevelCount &&
                     first.FirstArrayLayer + first.ArrayLayerCount == second.FirstArrayLayer)
            {
                range = first with
                {
                    ArrayLayerCount = checked(first.ArrayLayerCount + second.ArrayLayerCount),
                };
            }
            else
            {
                return false;
            }

            if (!left.IsTransition)
            {
                merged = PlannedBarrier.TextureUnorderedAccess(left.Resource, range);
                return true;
            }

            merged = left with { TextureRange = range };
            return true;
        }
    }

    private struct PassBarrierChain
    {
        public int Head;
        public int Count;
    }

    private struct PassBarrierEntry
    {
        public PassBarrierEntry(PlannedBarrier barrier, long order, int previous)
        {
            Barrier = barrier;
            Order = order;
            Previous = previous;
        }

        public PlannedBarrier Barrier;
        public long Order;
        public int Previous;
    }

    private unsafe struct PassPredecessorTable
    {
        private readonly ulong* _bits;
        private readonly int _passCount;
        private readonly int _wordCount;
        private int _entryCount;

        public PassPredecessorTable(RenderGraph graph, int passCount)
        {
            _passCount = passCount;
            _wordCount = checked((passCount + 63) >> 6);
            ArenaSlice<ulong> bitStorage = graph.AllocateSlice<ulong>(
                checked(passCount * _wordCount));
            _bits = bitStorage.DangerousPointer;
            _entryCount = 0;
        }

        public void Add(int pass, int predecessor)
        {
            if ((uint)pass >= (uint)_passCount)
                throw new ArgumentOutOfRangeException(nameof(pass));
            if ((uint)predecessor >= (uint)_passCount)
                throw new ArgumentOutOfRangeException(nameof(predecessor));
            int bitOrdinal = checked(pass * _wordCount + (predecessor >> 6));
            ulong mask = 1UL << (predecessor & 63);
            if ((_bits[bitOrdinal] & mask) != 0) return;
            _bits[bitOrdinal] |= mask;
            _entryCount++;
        }

        public ArenaSlice<int> CopyToSlice(RenderGraph graph, int pass)
        {
            if ((uint)pass >= (uint)_passCount)
                throw new ArgumentOutOfRangeException(nameof(pass));
            int count = Count(pass);
            ArenaSlice<int> result =
                graph.AllocateSlice<int>(count, clear: false);
            int destination = 0;
            ReadOnlySpan<ulong> words = GetWords(pass);
            for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                ulong word = words[wordIndex];
                while (word != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                    result[destination++] = checked((wordIndex << 6) + bit);
                    word &= word - 1;
                }
            }
            return result;
        }

        public void WriteToDependencies(
            RenderGraph graph,
            bool liveOnly)
        {
            graph.DependencyRows.Clear();
            graph.DependencyRows.EnsureCapacity(_entryCount);
            for (int pass = 0; pass < _passCount; pass++)
            {
                ref PassData row = ref graph.Passes[pass];
                row.DependencyOffset = graph.DependencyRows.Count;
                row.DependencyCount = 0;
                if (liveOnly && !graph.IsPassLive(pass)) continue;
                ReadOnlySpan<ulong> words = GetWords(pass);
                for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
                {
                    ulong word = words[wordIndex];
                    while (word != 0)
                    {
                        int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                        graph.DependencyRows.Add(checked((wordIndex << 6) + bit));
                        row.DependencyCount++;
                        word &= word - 1;
                    }
                }
            }
        }

        public readonly ReadOnlySpan<ulong> GetWords(int pass)
        {
            if ((uint)pass >= (uint)_passCount)
                throw new ArgumentOutOfRangeException(nameof(pass));
            return new ReadOnlySpan<ulong>(
                _bits + checked(pass * _wordCount),
                _wordCount);
        }

        public readonly int EntryCount => _entryCount;

        public readonly bool Contains(int pass, int predecessor)
        {
            int bitOrdinal = checked(pass * _wordCount + (predecessor >> 6));
            return (_bits[bitOrdinal] & (1UL << (predecessor & 63))) != 0;
        }

        private readonly int Count(int pass)
        {
            int count = 0;
            foreach (ulong word in GetWords(pass))
                count = checked(count + System.Numerics.BitOperations.PopCount(word));
            return count;
        }
    }

    private static bool QueueSupportsBarrier(QueueType queue, GraphResourceUsage before, GraphResourceUsage after) =>
        queue == QueueType.Graphics ||
        QueueSupportsState(queue, before) && QueueSupportsState(queue, after);

    private static bool QueueSupportsState(QueueType queue, GraphResourceUsage state) => queue switch
    {
        QueueType.Graphics => Enum.IsDefined(state),
        QueueType.Compute => state is GraphResourceUsage.Common or
            GraphResourceUsage.CopySource or GraphResourceUsage.CopyDestination or
            GraphResourceUsage.ShaderResource or GraphResourceUsage.UnorderedAccess or
            GraphResourceUsage.VertexOrConstantBuffer or GraphResourceUsage.IndirectArgument or GraphResourceUsage.AccelerationStructure,
        QueueType.Copy => state is GraphResourceUsage.Common or
            GraphResourceUsage.CopySource or GraphResourceUsage.CopyDestination,
        _ => false,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static GraphResourceUsage DesiredState(in PassInputData access) =>
        access.State;

    internal static GraphResourceUsage DesiredState(RenderGraph graph, in PassData pass, in PassInputData access)
    {
        return access.IsBuffer
            ? access.State
            : DesiredTextureState(graph, pass, access);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static GraphResourceUsage DesiredTextureState(
        RenderGraph graph,
        in PassData pass,
        in PassInputData access)
    {
        GraphResourceUsage desired = access.State;
        if (access.State is not (GraphResourceUsage.DepthRead or GraphResourceUsage.ShaderResource))
            return desired;
        GraphResourceUsage counterpart = access.State == GraphResourceUsage.DepthRead
            ? GraphResourceUsage.ShaderResource
            : GraphResourceUsage.DepthRead;
        int resource = access.Texture;
        TextureSubresourceRange range = access.TextureRange;
        foreach (ref readonly PassInputData candidate in graph.GetPassAccesses(pass))
        {
            if (!candidate.IsBuffer &&
                candidate.Texture == resource &&
                candidate.State == counterpart &&
                TextureRangesOverlap(range, candidate.TextureRange))
                return GraphResourceUsage.DepthReadShaderResource;
        }
        return desired;
    }

    private static bool TextureRangesOverlap(
        in TextureSubresourceRange left,
        in TextureSubresourceRange right) =>
        (left.Aspects & right.Aspects) != 0 &&
        left.FirstMipLevel < right.FirstMipLevel + right.MipLevelCount &&
        right.FirstMipLevel < left.FirstMipLevel + left.MipLevelCount &&
        left.FirstArrayLayer < right.FirstArrayLayer + right.ArrayLayerCount &&
        right.FirstArrayLayer < left.FirstArrayLayer + left.ArrayLayerCount;

    private static GraphResourceUsage InitialState(RenderGraph graph, int resource)
    {
        if (graph.IsBufferResourceOrdinal(resource))
        {
            ResourceUnversionedData buffer = graph.GetBufferByResourceOrdinal(resource);
            if (!buffer.IsImported)
                return InitialBufferState(
                    graph.GetBufferDescription(resource),
                    buffer.MemoryType);
            return buffer.InitialState;
        }
        ResourceUnversionedData texture = graph.GetTextureByResourceOrdinal(resource);
        if (!texture.IsImported) return GraphResourceUsage.Common;
        return texture.InitialState;
    }

    private static GraphResourceUsage InitialBufferState(in BufferDesc desc, MemoryType memoryType) =>
        (desc.Usages & BufferUsages.AccelerationStructure) != 0
            ? GraphResourceUsage.AccelerationStructure
            : memoryType switch
            {
                MemoryType.DeviceLocal => GraphResourceUsage.Common,
                MemoryType.Upload => GraphResourceUsage.CopySource,
                MemoryType.Readback => GraphResourceUsage.CopyDestination,
                _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
            };

    private static GraphResourceUsage FinalState(RenderGraph graph, int resource)
    {
        if (graph.IsBufferResourceOrdinal(resource))
        {
            return graph.GetBufferByResourceOrdinal(resource).FinalState;
        }
        return graph.GetTextureByResourceOrdinal(resource).FinalState;
    }

    private static TextureAspects PlanesFor(Format format) => GraphFormat.AllowedAspects(format);

    private static int TextureCellCount(in GraphTextureDescription desc) => checked(
        desc.MipLevels * desc.ArrayLayers * (GraphFormat.HasStencil(desc.Format) ? 2 : 1));

    private static TextureCellEnumerable EnumerateCells(in TextureSubresourceRange range) => new(range);

    private readonly struct TextureCellEnumerable
    {
        private readonly TextureSubresourceRange _range;

        public TextureCellEnumerable(in TextureSubresourceRange range) => _range = range;

        public Enumerator GetEnumerator() => new(_range);

        public struct Enumerator
        {
            private readonly int _firstMip;
            private readonly int _lastMip;
            private readonly int _lastLayer;
            private readonly byte _planes;
            private int _mip;
            private int _layer;
            private byte _remainingPlanes;

            public Enumerator(in TextureSubresourceRange range)
            {
                _firstMip = checked((int)range.FirstMipLevel);
                _lastMip = checked((int)(range.FirstMipLevel + range.MipLevelCount));
                _lastLayer = checked((int)(range.FirstArrayLayer + range.ArrayLayerCount));
                _planes = (byte)range.Aspects;
                _mip = checked((int)range.FirstMipLevel);
                _layer = checked((int)range.FirstArrayLayer);
                _remainingPlanes = _planes;
                Current = default;
            }

            public TextureCell Current { get; private set; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                while (_layer < _lastLayer)
                {
                    while (_mip < _lastMip)
                    {
                        if (_remainingPlanes != 0)
                        {
                            int planeBit =
                                System.Numerics.BitOperations.TrailingZeroCount(
                                    (uint)_remainingPlanes);
                            _remainingPlanes &=
                                (byte)(_remainingPlanes - 1);
                            GraphTextureAspect plane =
                                (GraphTextureAspect)planeBit;
                            Current = new TextureCell(_mip, _layer, plane);
                            return true;
                        }
                        _remainingPlanes = _planes;
                        _mip++;
                    }
                    _mip = _firstMip;
                    _layer++;
                }
                return false;
            }
        }
    }

    private readonly struct TextureBarrierTracker
    {
        public TextureBarrierTracker(
            RenderGraph graph,
            in GraphTextureDescription desc,
            GraphResourceUsage initial)
        {
            int planes = GraphFormat.HasStencil(desc.Format) ? 2 : 1;
            int count = checked(desc.MipLevels * desc.ArrayLayers * planes);
            States = graph.AllocateSlice<GraphResourceUsage>(count, clear: false);
            States.Span.Fill(initial);
            LastPass = graph.AllocateSlice<int>(count, clear: false);
            LastPass.Span.Fill(-1);
            LastEffect = graph.AllocateSlice<GraphAccess>(count, clear: false);
        }

        public ArenaSlice<GraphResourceUsage> States { get; }
        public ArenaSlice<int> LastPass { get; }
        public ArenaSlice<GraphAccess> LastEffect { get; }
    }

    private readonly record struct TextureCell(int Mip, int Layer, GraphTextureAspect Plane)
    {
        public TextureSubresourceRange Range => new(
            checked((uint)Mip),
            1,
            checked((uint)Layer),
            1,
            Plane switch
            {
                GraphTextureAspect.Color => TextureAspects.Color,
                GraphTextureAspect.Depth => TextureAspects.Depth,
                GraphTextureAspect.Stencil => TextureAspects.Stencil,
                _ => throw new ArgumentOutOfRangeException(),
            });
        public int Index(in GraphTextureDescription desc)
        {
            int plane = Plane == GraphTextureAspect.Stencil ? 1 : 0;
            return checked(Mip + Layer * desc.MipLevels + plane * desc.MipLevels * desc.ArrayLayers);
        }
    }

}

internal readonly record struct CompilerCpuTimings(
    TimeSpan Contents,
    TimeSpan Liveness,
    TimeSpan Validation,
    TimeSpan Dependencies,
    TimeSpan Barrier,
    TimeSpan Placement,
    TimeSpan Execution);
