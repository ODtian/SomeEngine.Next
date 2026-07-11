namespace SomeEngine.RenderGraph;

internal static class CompiledGraphContract
{
    public static void Validate(
        FrozenGraph source,
        CompiledGraph result,
        DeviceCompilationSnapshot device,
        bool optimized)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(device);
        if (result.Optimized != optimized)
        {
            throw new InvalidOperationException(optimized
                ? "The optimized graph compiler returned a non-optimized plan."
                : "The conservative graph compiler returned an optimized plan.");
        }

        ValidateTopLevelShape(source, result);
        GraphLiveness expected = GraphLiveness.Analyze(source);
        if (!result.ActivePassOrdinals.AsSpan().SequenceEqual(expected.ActivePassOrdinals) ||
            !result.RootPasses.AsSpan().SequenceEqual(expected.Roots) ||
            !result.RetainingPasses.AsSpan().SequenceEqual(expected.RetainingPasses) ||
            !result.LiveResources.AsSpan().SequenceEqual(expected.Resources) ||
            !result.LiveBufferViews.AsSpan().SequenceEqual(expected.BufferViews) ||
            !result.LiveTextureViews.AsSpan().SequenceEqual(expected.TextureViews))
        {
            throw new InvalidOperationException(
                "A compiled plan may not change the live pass, resource, or view set selected by exact graph semantics.");
        }

        ValidateQueuesAndDependencies(source, result, device, expected);
        ExecutionTopology topology = ValidateExecutionTopology(source, result, device, expected);
        ValidatePlacements(source, result, expected);
        ValidateBarrierProgram(source, result, topology, expected);
    }

    private static void ValidateTopLevelShape(FrozenGraph source, CompiledGraph result)
    {
        if (result.Queues is null ||
            result.ActivePassOrdinals is null ||
            result.RootPasses is null ||
            result.RetainingPasses is null ||
            result.LiveResources is null ||
            result.LiveBufferViews is null ||
            result.LiveTextureViews is null ||
            result.ExecutionBatches is null ||
            result.RecordUnits is null ||
            result.PassToRecordUnit is null ||
            result.Dependencies is null ||
            result.BeforeBarriers is null ||
            result.AfterBarriers is null ||
            result.Heaps is null ||
            result.Placements is null ||
            result.Rendering is null)
        {
            throw new InvalidOperationException("Compiled graph payload arrays cannot be null.");
        }

        int passCount = source.Passes.Length;
        if (result.Queues.Length != passCount ||
            result.RootPasses.Length != passCount ||
            result.RetainingPasses.Length != passCount ||
            result.Dependencies.Length != passCount ||
            result.BeforeBarriers.Length != passCount ||
            result.AfterBarriers.Length != passCount ||
            result.Rendering.Length != passCount ||
            result.PassToRecordUnit.Length != passCount ||
            result.Placements.Length != source.Resources.Length ||
            result.LiveResources.Length != source.Resources.Length ||
            result.LiveBufferViews.Length != source.BufferViews.Length ||
            result.LiveTextureViews.Length != source.TextureViews.Length)
        {
            throw new InvalidOperationException("The compiled graph payload shape does not match its frozen source graph.");
        }
        if (result.Raster.BreakReasonCounts is null ||
            result.Raster.BreakReasonCounts.Length != Enum.GetValues<RasterMergeBreakReason>().Length)
        {
            throw new InvalidOperationException("Compiled raster diagnostics have an invalid break-reason shape.");
        }
        for (int pass = 0; pass < passCount; pass++)
        {
            if (result.Dependencies[pass] is null ||
                result.BeforeBarriers[pass] is null ||
                result.AfterBarriers[pass] is null)
            {
                throw new InvalidOperationException("Compiled per-pass payload arrays cannot be null.");
            }
        }
    }

    private static void ValidateQueuesAndDependencies(
        FrozenGraph source,
        CompiledGraph result,
        DeviceCompilationSnapshot device,
        GraphLiveness expected)
    {
        for (int pass = 0; pass < source.Passes.Length; pass++)
        {
            QueueType queue = result.Queues[pass];
            if (!Enum.IsDefined(queue))
                throw new InvalidOperationException("A compiled pass selects an invalid queue.");
            if (!expected.Passes[pass]) continue;
            QueueType selected = source.Passes[pass].Queues.Select(device);
            if (queue != selected || !device.Supports(queue))
                throw new InvalidOperationException("A compiled live pass selects a queue outside its source/device contract.");
        }

        int[][] sourceDependencies = Compiler.BuildDependencies(source, result.Queues, expected);
        for (int pass = 0; pass < source.Passes.Length; pass++)
        {
            if (!result.Dependencies[pass].AsSpan().SequenceEqual(sourceDependencies[pass]))
            {
                throw new InvalidOperationException(
                    "Compiled logical-pass dependencies do not match the frozen graph's exact hazards and queue transfers.");
            }
        }
    }

    private static ExecutionTopology ValidateExecutionTopology(
        FrozenGraph source,
        CompiledGraph result,
        DeviceCompilationSnapshot device,
        GraphLiveness expected)
    {
        bool[] seenPasses = new bool[source.Passes.Length];
        bool[] seenUnits = new bool[result.RecordUnits.Length];
        for (int unitOrdinal = 0; unitOrdinal < result.RecordUnits.Length; unitOrdinal++)
        {
            CompiledRecordUnit unit = result.RecordUnits[unitOrdinal];
            if (!Enum.IsDefined(unit.Kind) || !Enum.IsDefined(unit.Queue) || !device.Supports(unit.Queue) ||
                unit.LogicalPassOrdinals is null || unit.AliasAcquires is null || unit.InternalBarriers is null)
            {
                throw new InvalidOperationException("A compiled record unit has an invalid kind, queue, or payload shape.");
            }

            bool validShape = unit.Kind switch
            {
                CompiledRecordUnitKind.Standalone =>
                    unit.LogicalPassOrdinals.Length == 1 && unit.AliasAcquires.Length == 0 && unit.InternalBarriers.Length == 0,
                CompiledRecordUnitKind.RasterScope =>
                    unit.Queue == QueueType.Graphics && unit.LogicalPassOrdinals.Length >= 2 &&
                    unit.AliasAcquires.Length == 0 && unit.InternalBarriers.Length == 0,
                CompiledRecordUnitKind.AliasAcquire =>
                    unit.Queue == QueueType.Graphics && unit.LogicalPassOrdinals.Length == 0 &&
                    unit.AliasAcquires.Length != 0 && unit.InternalBarriers.Length == 0,
                CompiledRecordUnitKind.InternalBarriers =>
                    unit.Queue == QueueType.Graphics && unit.LogicalPassOrdinals.Length == 0 &&
                    unit.AliasAcquires.Length == 0 && unit.InternalBarriers.Length != 0,
                _ => false,
            };
            if (!validShape)
                throw new InvalidOperationException("Compiled record-unit kind and payload cardinality are inconsistent.");

            int priorPass = -1;
            foreach (int pass in unit.LogicalPassOrdinals)
            {
                if ((uint)pass >= (uint)source.Passes.Length || !expected.Passes[pass] || seenPasses[pass] || pass <= priorPass)
                    throw new InvalidOperationException("Compiled record units must partition the ordered active logical-pass set.");
                if (result.PassToRecordUnit[pass] != unitOrdinal || result.Queues[pass] != unit.Queue)
                    throw new InvalidOperationException("Compiled pass-to-record-unit or queue ownership is inconsistent.");
                seenPasses[pass] = true;
                priorPass = pass;
            }

            if (unit.Kind == CompiledRecordUnitKind.RasterScope)
                ValidateRasterScope(source, result, unit);

            foreach (CompiledAliasAcquire acquire in unit.AliasAcquires)
            {
                if ((uint)acquire.BeforeResource >= (uint)source.Resources.Length ||
                    (uint)acquire.AfterResource >= (uint)source.Resources.Length ||
                    acquire.BeforeResource == acquire.AfterResource ||
                    source.Resources[acquire.BeforeResource].IsImported ||
                    source.Resources[acquire.AfterResource].IsImported ||
                    !expected.Resources[acquire.BeforeResource] ||
                    !expected.Resources[acquire.AfterResource])
                {
                    throw new InvalidOperationException("Alias acquire references invalid or non-transient live resources.");
                }
                CompiledPlacement beforePlacement = result.Placements[acquire.BeforeResource];
                CompiledPlacement afterPlacement = result.Placements[acquire.AfterResource];
                if (!beforePlacement.IsPlaced || beforePlacement != afterPlacement)
                    throw new InvalidOperationException("Alias acquire resources must share one exact physical placement.");
            }
        }

        for (int pass = 0; pass < source.Passes.Length; pass++)
        {
            if (seenPasses[pass] != expected.Passes[pass] ||
                (!expected.Passes[pass] && result.PassToRecordUnit[pass] != -1))
            {
                throw new InvalidOperationException("Compiled record units do not exactly cover graph liveness.");
            }
        }

        int[] unitToBatch = Enumerable.Repeat(-1, result.RecordUnits.Length).ToArray();
        int[] unitPosition = Enumerable.Repeat(-1, result.RecordUnits.Length).ToArray();
        bool[][] batchAncestors = Enumerable.Range(0, result.ExecutionBatches.Length)
            .Select(_ => new bool[result.ExecutionBatches.Length])
            .ToArray();
        for (int batchOrdinal = 0; batchOrdinal < result.ExecutionBatches.Length; batchOrdinal++)
        {
            CompiledExecutionBatch batch = result.ExecutionBatches[batchOrdinal];
            if (!Enum.IsDefined(batch.Queue) || !device.Supports(batch.Queue) ||
                batch.Dependencies is null || batch.RecordUnits is null || batch.RecordUnits.Length == 0)
            {
                throw new InvalidOperationException("A compiled execution batch has an invalid queue or payload shape.");
            }
            int previousDependency = -1;
            foreach (int dependency in batch.Dependencies)
            {
                if (dependency <= previousDependency || dependency >= batchOrdinal)
                    throw new InvalidOperationException("Execution-batch dependencies must be sorted, unique, and point backward.");
                previousDependency = dependency;
                batchAncestors[batchOrdinal][dependency] = true;
                for (int ancestor = 0; ancestor < dependency; ancestor++)
                    if (batchAncestors[dependency][ancestor]) batchAncestors[batchOrdinal][ancestor] = true;
            }

            int previousUnit = -1;
            for (int position = 0; position < batch.RecordUnits.Length; position++)
            {
                int unit = batch.RecordUnits[position];
                if ((uint)unit >= (uint)result.RecordUnits.Length || seenUnits[unit] ||
                    result.RecordUnits[unit].Queue != batch.Queue || unit <= previousUnit)
                {
                    throw new InvalidOperationException("Execution batches must partition ordered same-queue record units.");
                }
                seenUnits[unit] = true;
                unitToBatch[unit] = batchOrdinal;
                unitPosition[unit] = position;
                previousUnit = unit;
            }
        }
        if (seenUnits.Any(static seen => !seen))
            throw new InvalidOperationException("Every compiled record unit must belong to one execution batch.");

        ExecutionTopology topology = new(result, unitToBatch, unitPosition, batchAncestors);
        foreach (int pass in expected.ActivePassOrdinals)
        foreach (int predecessor in result.Dependencies[pass])
        {
            int predecessorUnit = result.PassToRecordUnit[predecessor];
            int passUnit = result.PassToRecordUnit[pass];
            if (!topology.HappensBefore(predecessorUnit, passUnit))
            {
                throw new InvalidOperationException(
                    "The execution-batch DAG does not cover a frozen source dependency.");
            }
        }
        return topology;
    }

    private static void ValidateRasterScope(
        FrozenGraph source,
        CompiledGraph result,
        in CompiledRecordUnit unit)
    {
        int firstPass = unit.LogicalPassOrdinals[0];
        CompiledRendering rendering = result.Rendering[firstPass] ??
            throw new InvalidOperationException("A raster-scope record unit starts with a non-raster pass.");
        FrozenPass first = source.Passes[firstPass];
        for (int index = 0; index < unit.LogicalPassOrdinals.Length; index++)
        {
            int pass = unit.LogicalPassOrdinals[index];
            FrozenPass current = source.Passes[pass];
            if (result.Rendering[pass] != rendering || current.RecordingLane != first.RecordingLane ||
                current.ColorAttachments.Length != first.ColorAttachments.Length)
            {
                throw new InvalidOperationException("A raster scope combines incompatible rendering or recording-lane shapes.");
            }
            if (index != 0 && result.BeforeBarriers[pass].Length != 0 ||
                index != unit.LogicalPassOrdinals.Length - 1 && result.AfterBarriers[pass].Length != 0)
            {
                throw new InvalidOperationException("A raster scope contains a barrier at an internal logical-pass boundary.");
            }
            for (int color = 0; color < current.ColorAttachments.Length; color++)
            {
                if (current.ColorAttachments[color].View != first.ColorAttachments[color].View ||
                    index != 0 && current.ColorAttachments[color].Load != LoadAction.Load)
                {
                    throw new InvalidOperationException("A raster scope changes its attachment set or reload contract.");
                }
            }
            if (!SameDepthStencilScope(first.DepthStencilAttachment, current.DepthStencilAttachment, index == 0))
                throw new InvalidOperationException("A raster scope changes its depth-stencil attachment contract.");
        }
    }

    private static bool SameDepthStencilScope(
        FrozenDepthStencilAttachment? first,
        FrozenDepthStencilAttachment? current,
        bool firstPass)
    {
        if (first is null || current is null) return first is null && current is null;
        if (first.Value.View != current.Value.View) return false;
        if (!SameDepthStencilMode(first.Value.Depth, current.Value.Depth, firstPass)) return false;
        return SameDepthStencilMode(first.Value.Stencil, current.Value.Stencil, firstPass);
    }

    private static bool SameDepthStencilMode<T>(T? first, T? current, bool firstPass)
        where T : struct
    {
        if (first is null || current is null) return first is null && current is null;
        return (first, current) switch
        {
            (DepthAttachmentOps left, DepthAttachmentOps right) =>
                left.ReadOnly == right.ReadOnly && (firstPass || right.Load == LoadAction.Load),
            (StencilAttachmentOps left, StencilAttachmentOps right) =>
                left.ReadOnly == right.ReadOnly && (firstPass || right.Load == LoadAction.Load),
            _ => false,
        };
    }

    private static void ValidatePlacements(FrozenGraph source, CompiledGraph result, GraphLiveness expected)
    {
        for (int heap = 0; heap < result.Heaps.Length; heap++)
        {
            CompiledHeap value = result.Heaps[heap];
            if (value.Size == 0 || !Enum.IsDefined(value.MemoryType) || !Enum.IsDefined(value.ResourceClass))
                throw new InvalidOperationException("A compiled transient heap has an invalid size or allocation profile.");
        }

        for (int resource = 0; resource < source.Resources.Length; resource++)
        {
            FrozenResource value = source.Resources[resource];
            CompiledPlacement placement = result.Placements[resource];
            bool mustBePlaced = expected.Resources[resource] && !value.IsImported;
            if (placement.IsPlaced != mustBePlaced)
                throw new InvalidOperationException("Only live transient resources may own compiled placements.");
            if (!mustBePlaced) continue;
            if ((uint)placement.Heap >= (uint)result.Heaps.Length)
                throw new InvalidOperationException("A compiled placement references an invalid heap.");

            ResourceRequirements requirements = value.Requirements;
            CompiledHeap heap = result.Heaps[placement.Heap];
            if (requirements.Alignment == 0 || placement.Offset % requirements.Alignment != 0 ||
                heap.MemoryType != requirements.MemoryType ||
                heap.ResourceClass != requirements.ResourceClass ||
                heap.CompatibilityClass != requirements.CompatibilityClass)
            {
                throw new InvalidOperationException("A compiled placement violates its alignment or allocation profile.");
            }
            ulong end;
            try
            {
                end = checked(placement.Offset + requirements.Size);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("A compiled placement range overflows.", exception);
            }
            if (end > heap.Size)
                throw new InvalidOperationException("A compiled placement exceeds its heap bounds.");
        }

        int[] placed = Enumerable.Range(0, source.Resources.Length)
            .Where(resource => result.Placements[resource].IsPlaced)
            .ToArray();
        for (int rightIndex = 0; rightIndex < placed.Length; rightIndex++)
        for (int leftIndex = 0; leftIndex < rightIndex; leftIndex++)
        {
            int left = placed[leftIndex];
            int right = placed[rightIndex];
            CompiledPlacement leftPlacement = result.Placements[left];
            CompiledPlacement rightPlacement = result.Placements[right];
            if (leftPlacement.Heap != rightPlacement.Heap) continue;
            ulong leftEnd = checked(leftPlacement.Offset + source.Resources[left].Requirements.Size);
            ulong rightEnd = checked(rightPlacement.Offset + source.Resources[right].Requirements.Size);
            bool overlaps = leftPlacement.Offset < rightEnd && rightPlacement.Offset < leftEnd;
            if (!overlaps) continue;
            if (!result.Aliasing.Enabled || leftPlacement.Offset != rightPlacement.Offset)
                throw new InvalidOperationException("Compiled transient placements partially overlap without an alias slot.");
        }
    }

    private static void ValidateBarrierProgram(
        FrozenGraph source,
        CompiledGraph result,
        ExecutionTopology topology,
        GraphLiveness expected)
    {
        ResourceState[] bufferStates = new ResourceState[source.Resources.Length];
        int[] bufferLastUnits = Enumerable.Repeat(-1, source.Resources.Length).ToArray();
        TextureSimulation?[] textureStates = new TextureSimulation?[source.Resources.Length];
        for (int resource = 0; resource < source.Resources.Length; resource++)
        {
            FrozenResource value = source.Resources[resource];
            ResourceState initial = InitialState(value);
            if (value.Kind == ResourceNodeKind.Buffer) bufferStates[resource] = initial;
            else textureStates[resource] = new TextureSimulation(value.TextureDesc, initial);
        }

        for (int batch = 0; batch < result.ExecutionBatches.Length; batch++)
        foreach (int unitOrdinal in result.ExecutionBatches[batch].RecordUnits)
        {
            CompiledRecordUnit unit = result.RecordUnits[unitOrdinal];
            if (unit.Kind == CompiledRecordUnitKind.InternalBarriers)
            {
                foreach (BarrierTemplate barrier in unit.InternalBarriers)
                    ApplyBarrier(source, result, topology, unitOrdinal, unit.Queue, barrier, bufferStates, bufferLastUnits, textureStates);
                continue;
            }
            foreach (int pass in unit.LogicalPassOrdinals)
            {
                foreach (BarrierTemplate barrier in result.BeforeBarriers[pass])
                    ApplyBarrier(source, result, topology, unitOrdinal, unit.Queue, barrier, bufferStates, bufferLastUnits, textureStates);
                foreach (FrozenAccess access in source.Passes[pass].Accesses)
                    ValidateAccessState(source, topology, unitOrdinal, access, bufferStates, bufferLastUnits, textureStates);
                foreach (BarrierTemplate barrier in result.AfterBarriers[pass])
                    ApplyBarrier(source, result, topology, unitOrdinal, unit.Queue, barrier, bufferStates, bufferLastUnits, textureStates);
            }
        }

        for (int resource = 0; resource < source.Resources.Length; resource++)
        {
            FrozenResource value = source.Resources[resource];
            if (!value.IsImported || !expected.Resources[resource]) continue;
            ResourceState final = FinalState(value);
            if (value.Kind == ResourceNodeKind.Buffer)
            {
                if (bufferStates[resource] != final)
                    throw new InvalidOperationException("A live imported buffer does not finish in its requested whole-resource final state.");
            }
            else if (textureStates[resource]!.States.Any(state => state != final))
            {
                throw new InvalidOperationException("A live imported texture does not finish every subresource in its requested final state.");
            }
        }
    }

    private static void ValidateAccessState(
        FrozenGraph source,
        ExecutionTopology topology,
        int unit,
        in FrozenAccess access,
        ResourceState[] bufferStates,
        int[] bufferLastUnits,
        TextureSimulation?[] textureStates)
    {
        ResourceState desired = Compiler.DesiredState(access);
        if (access.Kind == ResourceNodeKind.Buffer)
        {
            RequireOrdered(topology, bufferLastUnits[access.Resource], unit);
            if (bufferStates[access.Resource] != desired)
                throw new InvalidOperationException("A compiled buffer access is not preceded by its required resource state.");
            bufferLastUnits[access.Resource] = unit;
            return;
        }

        TextureSimulation states = textureStates[access.Resource]!;
        foreach (int cell in EnumerateCellIndices(source.Resources[access.Resource].TextureDesc, access.TextureRange))
        {
            RequireOrdered(topology, states.LastUnits[cell], unit);
            if (states.States[cell] != desired)
                throw new InvalidOperationException("A compiled texture access is not preceded by its required subresource state.");
            states.LastUnits[cell] = unit;
        }
    }

    private static void ApplyBarrier(
        FrozenGraph source,
        CompiledGraph result,
        ExecutionTopology topology,
        int unit,
        QueueType queue,
        in BarrierTemplate barrier,
        ResourceState[] bufferStates,
        int[] bufferLastUnits,
        TextureSimulation?[] textureStates)
    {
        if ((uint)barrier.Resource >= (uint)source.Resources.Length || !result.LiveResources[barrier.Resource] ||
            !Enum.IsDefined(barrier.Kind) || !Enum.IsDefined(barrier.Before) || !Enum.IsDefined(barrier.After))
        {
            throw new InvalidOperationException("A compiled barrier references an invalid kind, state, or live resource.");
        }
        if (barrier.Kind == BarrierKind.Aliasing)
            throw new InvalidOperationException("Aliasing barriers must be represented by alias-acquire record units.");
        if (!QueueSupportsBarrier(queue, barrier.Before, barrier.After))
            throw new InvalidOperationException("A compiled transition uses states that are illegal on its command queue.");

        FrozenResource resource = source.Resources[barrier.Resource];
        if (resource.Kind == ResourceNodeKind.Buffer)
        {
            if (barrier.TextureRange != default)
                throw new InvalidOperationException("A buffer barrier cannot carry a texture subresource range.");
            RequireOrdered(topology, bufferLastUnits[barrier.Resource], unit);
            ValidateAndApplyState(barrier, ref bufferStates[barrier.Resource]);
            bufferLastUnits[barrier.Resource] = unit;
            return;
        }

        TextureSimulation states = textureStates[barrier.Resource]!;
        int[] cells = EnumerateCellIndices(resource.TextureDesc, barrier.TextureRange).ToArray();
        if (cells.Length == 0)
            throw new InvalidOperationException("A texture barrier must select at least one valid subresource.");
        foreach (int cell in cells)
        {
            RequireOrdered(topology, states.LastUnits[cell], unit);
            ResourceState state = states.States[cell];
            ValidateAndApplyState(barrier, ref state);
            states.States[cell] = state;
            states.LastUnits[cell] = unit;
        }
    }

    private static void ValidateAndApplyState(in BarrierTemplate barrier, ref ResourceState state)
    {
        switch (barrier.Kind)
        {
            case BarrierKind.Transition:
                if (barrier.Before == barrier.After || state != barrier.Before)
                    throw new InvalidOperationException("A compiled transition barrier does not match simulated resource state.");
                state = barrier.After;
                break;
            case BarrierKind.UnorderedAccess:
                if (barrier.Before != ResourceState.UnorderedAccess ||
                    barrier.After != ResourceState.UnorderedAccess ||
                    state != ResourceState.UnorderedAccess)
                {
                    throw new InvalidOperationException("A compiled unordered-access barrier has invalid state semantics.");
                }
                break;
            default:
                throw new InvalidOperationException("A compiled barrier has no valid state-machine lowering.");
        }
    }

    private static void RequireOrdered(ExecutionTopology topology, int priorUnit, int currentUnit)
    {
        if (priorUnit >= 0 && priorUnit != currentUnit && !topology.HappensBefore(priorUnit, currentUnit))
        {
            throw new InvalidOperationException(
                "Compiled cross-queue resource-state operations are not ordered by the execution-batch DAG.");
        }
    }

    private static bool QueueSupportsBarrier(QueueType queue, ResourceState before, ResourceState after) =>
        queue == QueueType.Graphics || QueueSupportsState(queue, before) && QueueSupportsState(queue, after);

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
        _ => throw new InvalidOperationException("An imported buffer has an invalid boundary use."),
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
        _ => throw new InvalidOperationException("An imported texture has an invalid boundary use."),
    };

    private static IEnumerable<int> EnumerateCellIndices(
        TextureDesc desc,
        TextureSubresourceRange range)
    {
        if (range.FirstMip < 0 || range.MipCount <= 0 ||
            range.FirstLayer < 0 || range.LayerCount <= 0 ||
            range.FirstMip + range.MipCount > desc.MipLevels ||
            range.FirstLayer + range.LayerCount > desc.ArrayLayers)
        {
            yield break;
        }
        TextureAspect allowed = desc.Format switch
        {
            Format.D32Float => TextureAspect.Depth,
            Format.D24UNormS8UInt => TextureAspect.Depth | TextureAspect.Stencil,
            _ => TextureAspect.Color,
        };
        if (range.Aspect == 0 || (range.Aspect & ~allowed) != 0) yield break;

        for (int layer = range.FirstLayer; layer < range.FirstLayer + range.LayerCount; layer++)
        for (int mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
        {
            if ((range.Aspect & TextureAspect.Color) != 0)
                yield return mip + layer * desc.MipLevels;
            if ((range.Aspect & TextureAspect.Depth) != 0)
                yield return mip + layer * desc.MipLevels;
            if ((range.Aspect & TextureAspect.Stencil) != 0)
                yield return mip + layer * desc.MipLevels + desc.MipLevels * desc.ArrayLayers;
        }
    }

    private sealed class TextureSimulation
    {
        public TextureSimulation(in TextureDesc desc, ResourceState initial)
        {
            int planes = desc.Format == Format.D24UNormS8UInt ? 2 : 1;
            int count = checked(desc.MipLevels * desc.ArrayLayers * planes);
            States = Enumerable.Repeat(initial, count).ToArray();
            LastUnits = Enumerable.Repeat(-1, count).ToArray();
        }

        public ResourceState[] States { get; }
        public int[] LastUnits { get; }
    }

    private sealed class ExecutionTopology
    {
        private readonly CompiledGraph _graph;
        private readonly int[] _unitToBatch;
        private readonly int[] _unitPosition;
        private readonly bool[][] _batchAncestors;

        public ExecutionTopology(
            CompiledGraph graph,
            int[] unitToBatch,
            int[] unitPosition,
            bool[][] batchAncestors)
        {
            _graph = graph;
            _unitToBatch = unitToBatch;
            _unitPosition = unitPosition;
            _batchAncestors = batchAncestors;
        }

        public bool HappensBefore(int leftUnit, int rightUnit)
        {
            if (leftUnit == rightUnit) return true;
            int leftBatch = _unitToBatch[leftUnit];
            int rightBatch = _unitToBatch[rightUnit];
            if (leftBatch == rightBatch)
                return _unitPosition[leftUnit] < _unitPosition[rightUnit];
            if (leftBatch >= rightBatch) return false;
            if (_graph.ExecutionBatches[leftBatch].Queue == _graph.ExecutionBatches[rightBatch].Queue)
                return true;
            return _batchAncestors[rightBatch][leftBatch];
        }
    }
}
