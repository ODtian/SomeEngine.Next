namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private const ulong MinimumSplitBarrierOverlapCost = 8;
    private List<BufferBarrier>[] _beforeBufferBarriers = [];
    private List<BufferBarrier>[] _afterBufferBarriers = [];
    private List<TextureBarrier>[] _beforeTextureBarriers = [];
    private List<TextureBarrier>[] _afterTextureBarriers = [];
    private List<QueueAcquire>[] _acquires = [];
    private List<QueueRelease>[] _releases = [];
    private List<AliasingResource>[] _aliasBeforeResources = [];
    private List<AliasingResource>[] _aliasAfterResources = [];
    private List<QueueCompletion>[] _completionWaits = [];
    private List<int>[] _physicalPredecessors = [];
    private readonly List<PendingQueueRelease> _lateReleases = [];
    private readonly HashSet<Queue> _transferSources =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<QueueType> _transferTypes = [];
    private readonly List<PendingBarrierTransition> _pendingBarrierTransitions = [];
    private TexturePhysicalState[] _texturePhysicalStateScratch = [];
    private bool[] _textureAssignedScratch = [];

    private void ResolveSynchronization()
    {
        int passCount = _passes.Length;
        PrepareLists(ref _beforeBufferBarriers, passCount);
        PrepareLists(ref _afterBufferBarriers, passCount);
        PrepareLists(ref _beforeTextureBarriers, passCount);
        PrepareLists(ref _afterTextureBarriers, passCount);
        PrepareLists(ref _acquires, passCount);
        PrepareLists(ref _releases, passCount);
        PrepareLists(ref _aliasBeforeResources, passCount);
        PrepareLists(ref _aliasAfterResources, passCount);
        PrepareLists(ref _completionWaits, passCount);
        PrepareLists(ref _physicalPredecessors, passCount);
        _pendingBarrierTransitions.Clear();

        ResolveAliasingBarriers();
        ResolveBufferSynchronization();
        ResolveTextureSynchronization();
        ResolveOpaqueSynchronization();
        PlacePendingBarrierTransitions();
    }

    private void ResolveOpaqueSynchronization()
    {
        for (int poolIndex = 0; poolIndex < _queryPools.Length; poolIndex++)
        {
            FrameQueryPool pool = _queryPools[poolIndex];
            List<int> accesses = CollectResourceAccesses(
                GraphAccessTargetKind.QueryPool,
                poolIndex,
                enabledOnly: false);
            FilterLiveAccesses(accesses);
            foreach (int accessIndex in accesses)
            {
                FrameResourceAccess access = _accesses[accessIndex];
                int pass = access.PassIndex;
                Queue queue = _passes[pass].Queue!;
                if (queue.Type != pool.Resource.Description.QueueType)
                {
                    throw new NotSupportedException(
                        $"QueryPool '{pool.Resource.Label}' requires a {pool.Resource.Description.QueueType} Queue.");
                }
                foreach (QueryBoundaryState endpoint in pool.EntryBoundaryStates)
                {
                    if (!Overlaps(endpoint.Range, access.QueryRange) ||
                        !endpoint.ReadyAfter.HasValue ||
                        ReferenceEquals(endpoint.ReadyAfter.Value.Queue, queue))
                        continue;
                    AddCompletion(_completionWaits[pass], endpoint.ReadyAfter.Value);
                }
            }
        }

        for (int tableIndex = 0; tableIndex < _shaderTables.Length; tableIndex++)
        {
            FrameRayTracingShaderTable table = _shaderTables[tableIndex];
            List<int> accesses = CollectResourceAccesses(
                GraphAccessTargetKind.RayTracingShaderTable,
                tableIndex,
                enabledOnly: false);
            FilterLiveAccesses(accesses);
            foreach (int accessIndex in accesses)
            {
                int pass = _accesses[accessIndex].PassIndex;
                Queue queue = _passes[pass].Queue!;
                foreach (RayTracingShaderTableBoundaryState endpoint in table.EntryBoundaryStates)
                {
                    if (!endpoint.ReadyAfter.HasValue ||
                        ReferenceEquals(endpoint.ReadyAfter.Value.Queue, queue))
                        continue;
                    AddCompletion(_completionWaits[pass], endpoint.ReadyAfter.Value);
                }
            }
        }
    }

    private void ResolveAliasingBarriers()
    {
        for (int buffer = 0; buffer < _buffers.Length; buffer++)
        {
            FrameBuffer resource = _buffers[buffer];
            if (resource.LastUse < 0 || resource.Placement.AliasingPredecessor is null)
                continue;
            int firstPass = _schedule[resource.FirstUse];
            AddAliasingBoundary(
                resource.Placement.AliasingPredecessor,
                resource.Resource!,
                firstPass);
        }
        for (int texture = 0; texture < _textures.Length; texture++)
        {
            FrameTexture resource = _textures[texture];
            if (resource.LastUse < 0 || resource.Placement.AliasingPredecessor is null)
                continue;
            int firstPass = _schedule[resource.FirstUse];
            AddAliasingBoundary(
                resource.Placement.AliasingPredecessor,
                resource.Resource!,
                firstPass);
        }
    }

    private void AddAliasingBoundary(
        Resource before,
        Resource after,
        int currentPass)
    {
        int previousPass = FindAliasingPredecessorLastPass(
            before,
            _passes[currentPass].Queue!);
        AliasingResource beforeResource = new(before);
        AliasingResource afterResource = new(after);
        if (previousPass >= 0)
        {
            _pendingBarrierTransitions.Add(PendingBarrierTransition.ForAliasing(
                previousPass,
                currentPass,
                _passes[currentPass].ScheduledOrdinal,
                beforeResource,
                afterResource));
            return;
        }
        _aliasBeforeResources[currentPass].Add(beforeResource);
        _aliasAfterResources[currentPass].Add(afterResource);
    }

    private int FindAliasingPredecessorLastPass(
        Resource predecessor,
        Queue targetQueue)
    {
        foreach (FrameBuffer buffer in _buffers)
        {
            if (!ReferenceEquals(buffer.Resource, predecessor) || buffer.LastUse < 0)
                continue;
            int pass = _schedule[buffer.LastUse];
            return ReferenceEquals(_passes[pass].Queue, targetQueue) ? pass : -1;
        }
        foreach (FrameTexture texture in _textures)
        {
            if (!ReferenceEquals(texture.Resource, predecessor) || texture.LastUse < 0)
                continue;
            int pass = _schedule[texture.LastUse];
            return ReferenceEquals(_passes[pass].Queue, targetQueue) ? pass : -1;
        }
        return -1;
    }

    private void ResolveBufferSynchronization()
    {
        for (int bufferIndex = 0; bufferIndex < _buffers.Length; bufferIndex++)
        {
            List<int> accesses = CollectResourceAccesses(GraphAccessTargetKind.Buffer, bufferIndex, enabledOnly: false);
            FilterLiveAccesses(accesses);
            if (accesses.Count == 0) continue;
            SortAccessesBySchedule(accesses);

            FrameBuffer resource = _buffers[bufferIndex];
            Buffer buffer = resource.Resource
                ?? throw new InvalidOperationException("A live Buffer was not materialized.");
            bool aliasActivated = resource.Placement.AliasingPredecessor is not null;
            int aliasPreviousPass = aliasActivated
                ? FindAliasingPredecessorLastPass(
                    resource.Placement.AliasingPredecessor!,
                    _passes[_accesses[accesses[0]].PassIndex].Queue!)
                : -1;
            BufferPhysicalState state = aliasActivated
                ? new BufferPhysicalState(
                    PipelineSync.None,
                    ResourceAccess.NoAccess,
                    null,
                    -1,
                    false)
                : InitialBufferState(resource, buffer);

            int cursor = 0;
            while (cursor < accesses.Count)
            {
                int pass = _accesses[accesses[cursor]].PassIndex;
                PipelineSync targetSync = PipelineSync.None;
                ResourceAccess targetAccess = ResourceAccess.NoAccess;
                bool targetWrites = false;
                int end = cursor;
                while (end < accesses.Count && _accesses[accesses[end]].PassIndex == pass)
                {
                    FrameResourceAccess access = _accesses[accesses[end]];
                    targetSync |= access.Sync;
                    targetAccess = MergeBufferAccess(targetAccess, access.Access);
                    targetWrites |= access.Mode != GraphAccessMode.Read;
                    end++;
                }

                Queue targetQueue = _passes[pass].Queue!;
                if (state.LastPass < 0)
                {
                    if (!aliasActivated)
                    {
                        AddInitialCompletions(resource.EntryBoundaryStates, targetQueue, pass);
                        ResolveInitialBufferTransfers(
                            resource.EntryBoundaryStates,
                            buffer,
                            targetQueue,
                            pass,
                            targetSync,
                            targetAccess);
                    }
                    state = state with { Queue = targetQueue };
                }
                bool queueChanged = state.Queue is not null && !ReferenceEquals(state.Queue, targetQueue);
                bool stateChanged = state.Sync != targetSync || state.Access != targetAccess;
                bool ordering = state.Writes || targetWrites;
                bool barrierFreeInitialAccess =
                    state.LastPass < 0 &&
                    resource.EntryBoundaryStates is not { Length: > 0 } &&
                    resource.Placement.AliasingPredecessor is null &&
                    state.Sync == PipelineSync.None &&
                    state.Access == ResourceAccess.NoAccess;
                if (queueChanged)
                {
                    if (state.LastPass >= 0)
                    {
                        AddUnique(_physicalPredecessors[pass], state.LastPass);
                        if (state.Queue!.Type != targetQueue.Type)
                        {
                            _releases[state.LastPass].Add(new QueueRelease(
                                buffer, null, state.Sync, state.Access, null, targetQueue.Type));
                            _acquires[pass].Add(new QueueAcquire(
                                buffer, null, state.Queue.Type, targetSync, targetAccess, null));
                        }
                    }
                    else if (state.Queue is not null && state.Queue.Type != targetQueue.Type)
                    {
                        _lateReleases.Add(new PendingQueueRelease(
                            state.Queue,
                            targetQueue,
                            pass,
                            buffer,
                            null,
                            state.Sync,
                            state.Access,
                            null));
                        _acquires[pass].Add(new QueueAcquire(
                            buffer, null, state.Queue.Type, targetSync, targetAccess, null));
                    }
                }

                if (!queueChanged || state.Queue?.Type == targetQueue.Type)
                {
                    if (!barrierFreeInitialAccess && (stateChanged || ordering))
                    {
                        AddBufferTransition(
                            state.LastPass < 0 && aliasPreviousPass >= 0
                                ? aliasPreviousPass
                                : state.LastPass,
                            pass,
                            buffer,
                            state.Sync,
                            targetSync,
                            state.Access,
                            targetAccess,
                            queueChanged);
                    }
                }

                state = new BufferPhysicalState(
                    targetSync,
                    targetAccess,
                    targetQueue,
                    pass,
                    targetWrites);
                cursor = end;
            }
        }
    }

    private void ResolveTextureSynchronization()
    {
        for (int textureIndex = 0; textureIndex < _textures.Length; textureIndex++)
        {
            List<int> accesses = CollectResourceAccesses(GraphAccessTargetKind.Texture, textureIndex, enabledOnly: false);
            FilterLiveAccesses(accesses);
            if (accesses.Count == 0) continue;
            SortAccessesBySchedule(accesses);

            FrameTexture resource = _textures[textureIndex];
            Texture texture = resource.Resource
                ?? throw new InvalidOperationException("A live Texture was not materialized.");
            int cellCount = TextureCellCount(resource);
            bool aliasActivated = resource.Placement.AliasingPredecessor is not null;
            int aliasPreviousPass = aliasActivated
                ? FindAliasingPredecessorLastPass(
                    resource.Placement.AliasingPredecessor!,
                    _passes[_accesses[accesses[0]].PassIndex].Queue!)
                : -1;
            Span<TexturePhysicalState> states = InitialTextureStates(
                resource,
                texture,
                cellCount,
                aliasActivated);
            if (!aliasActivated)
            {
                AddInitialCompletions(resource.EntryBoundaryStates,
                    _passes[_accesses[accesses[0]].PassIndex].Queue!,
                    _accesses[accesses[0]].PassIndex);
            }

            foreach (int accessIndex in accesses)
            {
                FrameResourceAccess access = _accesses[accessIndex];
                int pass = access.PassIndex;
                Queue targetQueue = _passes[pass].Queue!;
                foreach (int cell in TextureCells(resource, access.TextureRange))
                {
                    TexturePhysicalState state = states[cell];
                    bool targetWrites = access.Mode != GraphAccessMode.Read;
                    TextureSubresourceRange cellRange = TextureCellRange(resource, cell);
                    if (state.LastPass < 0)
                    {
                        if (!aliasActivated)
                        {
                            ResolveInitialTextureTransfers(
                                resource.EntryBoundaryStates,
                                texture,
                                cellRange,
                                targetQueue,
                                pass,
                                access.Sync,
                                access.Access,
                                access.TextureLayout);
                        }
                        state = state with { Queue = targetQueue };
                    }
                    bool queueChanged = state.Queue is not null && !ReferenceEquals(state.Queue, targetQueue);
                    bool stateChanged = state.Sync != access.Sync ||
                        state.Access != access.Access ||
                        state.Layout != access.TextureLayout;
                    bool ordering = state.Writes || targetWrites;
                    bool hoistableInitialTransition =
                        state.LastPass < 0 &&
                        resource.EntryBoundaryStates is not { Length: > 0 } &&
                        resource.Placement.AliasingPredecessor is null;
                    if (queueChanged)
                    {
                        if (state.LastPass >= 0)
                        {
                            AddUnique(_physicalPredecessors[pass], state.LastPass);
                            if (state.Queue!.Type != targetQueue.Type)
                            {
                                _releases[state.LastPass].Add(new QueueRelease(
                                    texture, cellRange, state.Sync, state.Access,
                                    state.Layout, targetQueue.Type));
                                _acquires[pass].Add(new QueueAcquire(
                                    texture, cellRange, state.Queue.Type, access.Sync,
                                    access.Access, access.TextureLayout));
                            }
                        }
                        else if (state.Queue is not null && state.Queue.Type != targetQueue.Type)
                        {
                            _lateReleases.Add(new PendingQueueRelease(
                                state.Queue,
                                targetQueue,
                                pass,
                                texture,
                                cellRange,
                                state.Sync,
                                state.Access,
                                state.Layout));
                            _acquires[pass].Add(new QueueAcquire(
                                texture, cellRange, state.Queue.Type, access.Sync,
                                access.Access, access.TextureLayout));
                        }
                    }

                    if (!queueChanged || state.Queue?.Type == targetQueue.Type)
                    {
                        if (stateChanged || ordering)
                        {
                            AddTextureTransition(
                                state.LastPass < 0 && aliasPreviousPass >= 0
                                    ? aliasPreviousPass
                                    : state.LastPass,
                                pass,
                                texture,
                                cellRange,
                                state.Sync,
                                access.Sync,
                                state.Access,
                                access.Access,
                                state.Layout,
                                access.TextureLayout,
                                queueChanged,
                                hoistableInitialTransition);
                        }
                    }

                    states[cell] = new TexturePhysicalState(
                        access.Sync,
                        access.Access,
                        access.TextureLayout,
                        targetQueue,
                        pass,
                        targetWrites);
                }
            }

            Queue? presentQueue = null;
            foreach ((GraphIdentity identity, _, Queue queue) in _frame.SwapchainImages)
            {
                if (identity != resource.Identity) continue;
                presentQueue = queue;
                break;
            }
            if (presentQueue is null) continue;

            for (int cell = 0; cell < states.Length; cell++)
            {
                TexturePhysicalState state = states[cell];
                if (state.LastPass < 0)
                {
                    if (state.Layout != TextureLayout.Present ||
                        state.Sync != PipelineSync.None ||
                        state.Access != ResourceAccess.NoAccess)
                    {
                        throw new InvalidOperationException(
                            "A submitted SwapchainImage must be written or explicitly returned to Present before submission.");
                    }
                    continue;
                }
                if (!ReferenceEquals(state.Queue, presentQueue))
                {
                    throw new InvalidOperationException(
                        "The final SwapchainImage use must execute on its presentation Queue.");
                }
                if (state.Sync == PipelineSync.None &&
                    state.Access == ResourceAccess.NoAccess &&
                    state.Layout == TextureLayout.Present)
                    continue;

                _afterTextureBarriers[state.LastPass].Add(new TextureBarrier(
                    texture,
                    TextureCellRange(resource, cell),
                    state.Sync,
                    PipelineSync.None,
                    state.Access,
                    ResourceAccess.NoAccess,
                    state.Layout,
                    TextureLayout.Present));
            }
        }
    }

    private void AddBufferTransition(
        int previousPass,
        int currentPass,
        Buffer buffer,
        PipelineSync beforeSync,
        PipelineSync afterSync,
        ResourceAccess beforeAccess,
        ResourceAccess afterAccess,
        bool queueChanged)
    {
        BufferBarrier barrier = new(
            buffer,
            beforeSync,
            afterSync,
            beforeAccess,
            afterAccess);
        if (!queueChanged && previousPass >= 0)
        {
            _pendingBarrierTransitions.Add(PendingBarrierTransition.ForBuffer(
                previousPass,
                currentPass,
                _passes[currentPass].ScheduledOrdinal,
                barrier));
            return;
        }
        _beforeBufferBarriers[currentPass].Add(barrier);
    }

    private void AddTextureTransition(
        int previousPass,
        int currentPass,
        Texture texture,
        in TextureSubresourceRange range,
        PipelineSync beforeSync,
        PipelineSync afterSync,
        ResourceAccess beforeAccess,
        ResourceAccess afterAccess,
        TextureLayout beforeLayout,
        TextureLayout afterLayout,
        bool queueChanged,
        bool hoistableInitialTransition)
    {
        bool split = !queueChanged &&
            beforeLayout != afterLayout &&
            ShouldSplitTransition(previousPass, currentPass);
        if (split)
        {
            _afterTextureBarriers[previousPass].Add(new TextureBarrier(
                texture, range, beforeSync, afterSync, beforeAccess, afterAccess,
                beforeLayout, afterLayout, BarrierPhase.Begin));
            _beforeTextureBarriers[currentPass].Add(new TextureBarrier(
                texture, range, beforeSync, afterSync, beforeAccess, afterAccess,
                beforeLayout, afterLayout, BarrierPhase.End));
        }
        else
        {
            TextureBarrier barrier = new(
                texture,
                range,
                beforeSync,
                afterSync,
                beforeAccess,
                afterAccess,
                beforeLayout,
                afterLayout);
            if (!queueChanged && previousPass >= 0 && !hoistableInitialTransition)
            {
                _pendingBarrierTransitions.Add(PendingBarrierTransition.ForTexture(
                    previousPass,
                    currentPass,
                    _passes[currentPass].ScheduledOrdinal,
                    barrier));
                return;
            }
            int placementPass = hoistableInitialTransition
                ? FindFirstQueuePass(currentPass)
                : currentPass;
            _beforeTextureBarriers[placementPass].Add(barrier);
        }
    }

    private void PlacePendingBarrierTransitions()
    {
        _pendingBarrierTransitions.Sort(static (left, right) =>
        {
            int byEnd = left.CurrentScheduledOrdinal.CompareTo(
                right.CurrentScheduledOrdinal);
            return byEnd != 0
                ? byEnd
                : left.PreviousPass.CompareTo(right.PreviousPass);
        });
        foreach (PendingBarrierTransition pending in _pendingBarrierTransitions)
        {
            int placementPass = FindExistingBarrierBoundary(
                pending.PreviousPass,
                pending.CurrentPass);
            switch (pending.Kind)
            {
                case PendingBarrierKind.Buffer:
                    _beforeBufferBarriers[placementPass].Add(pending.Buffer);
                    break;
                case PendingBarrierKind.Texture:
                    _beforeTextureBarriers[placementPass].Add(pending.Texture);
                    break;
                case PendingBarrierKind.Aliasing:
                    _aliasBeforeResources[placementPass].Add(pending.AliasBefore);
                    _aliasAfterResources[placementPass].Add(pending.AliasAfter);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        _pendingBarrierTransitions.Clear();
    }

    private int FindFirstQueuePass(int pass)
    {
        int current = pass;
        while (_sameQueuePredecessor[current] >= 0)
            current = _sameQueuePredecessor[current];
        return current;
    }

    private bool ShouldSplitTransition(int previousPass, int currentPass)
    {
        if (previousPass < 0 ||
            (_frame.Options.Debug & RenderGraphDebugOptions.DisableSplitBarriers) != 0)
        {
            return false;
        }

        ulong overlapCost = 0;
        int candidate = _sameQueuePredecessor[currentPass];
        while (candidate >= 0 && candidate != previousPass)
        {
            overlapCost = checked(
                overlapCost + _passes[candidate].Options.EstimatedExecutionCost);
            if (overlapCost >= MinimumSplitBarrierOverlapCost)
                return true;
            candidate = _sameQueuePredecessor[candidate];
        }
        return false;
    }

    private int FindExistingBarrierBoundary(int previousPass, int currentPass)
    {
        int candidate = currentPass;
        while (candidate >= 0 && candidate != previousPass)
        {
            if (_beforeBufferBarriers[candidate].Count != 0 ||
                _beforeTextureBarriers[candidate].Count != 0 ||
                _acquires[candidate].Count != 0 ||
                _aliasBeforeResources[candidate].Count != 0)
            {
                return candidate;
            }
            candidate = _sameQueuePredecessor[candidate];
        }
        return currentPass;
    }

    private readonly record struct PendingBarrierTransition(
        int PreviousPass,
        int CurrentPass,
        int CurrentScheduledOrdinal,
        PendingBarrierKind Kind,
        BufferBarrier Buffer,
        TextureBarrier Texture,
        AliasingResource AliasBefore,
        AliasingResource AliasAfter)
    {
        internal static PendingBarrierTransition ForBuffer(
            int previousPass,
            int currentPass,
            int currentScheduledOrdinal,
            in BufferBarrier barrier) => new(
                previousPass,
                currentPass,
                currentScheduledOrdinal,
                PendingBarrierKind.Buffer,
                barrier,
                default,
                default,
                default);

        internal static PendingBarrierTransition ForTexture(
            int previousPass,
            int currentPass,
            int currentScheduledOrdinal,
            in TextureBarrier barrier) => new(
                previousPass,
                currentPass,
                currentScheduledOrdinal,
                PendingBarrierKind.Texture,
                default,
                barrier,
                default,
                default);

        internal static PendingBarrierTransition ForAliasing(
            int previousPass,
            int currentPass,
            int currentScheduledOrdinal,
            in AliasingResource before,
            in AliasingResource after) => new(
                previousPass,
                currentPass,
                currentScheduledOrdinal,
                PendingBarrierKind.Aliasing,
                default,
                default,
                before,
                after);
    }

    private enum PendingBarrierKind : byte
    {
        Buffer,
        Texture,
        Aliasing,
    }

    private BufferPhysicalState InitialBufferState(in FrameBuffer resource, Buffer buffer)
    {
        if (resource.EntryBoundaryStates is not { Length: > 0 })
            return new BufferPhysicalState(
                buffer.InitialSync,
                buffer.InitialAccess,
                null,
                -1,
                false);
        PipelineSync sync = PipelineSync.None;
        ResourceAccess access = ResourceAccess.NoAccess;
        Queue? queue = null;
        foreach (BufferBoundaryState endpoint in resource.EntryBoundaryStates)
        {
            sync |= endpoint.Sync;
            access = MergeBufferAccess(access, endpoint.Access);
            if (queue is null) queue = endpoint.Queue;
            else if (endpoint.Queue is not null && !ReferenceEquals(queue, endpoint.Queue)) queue = null;
        }
        return new BufferPhysicalState(sync, access, queue, -1, ResourceAccessRules.Writes(access));
    }

    private Span<TexturePhysicalState> InitialTextureStates(
        in FrameTexture resource,
        Texture texture,
        int cellCount,
        bool aliasActivated)
    {
        EnsureCapacity(ref _texturePhysicalStateScratch, cellCount);
        EnsureCapacity(ref _textureAssignedScratch, cellCount);
        Array.Clear(_textureAssignedScratch, 0, cellCount);
        Span<TexturePhysicalState> result = _texturePhysicalStateScratch.AsSpan(0, cellCount);
        for (int cell = 0; cell < result.Length; cell++)
        {
            result[cell] = aliasActivated
                ? new TexturePhysicalState(
                    PipelineSync.None,
                    ResourceAccess.NoAccess,
                    TextureLayout.Undefined,
                    null,
                    -1,
                    false)
                : new TexturePhysicalState(
                    texture.InitialSync,
                    texture.InitialAccess,
                    texture.InitialLayout,
                    null,
                    -1,
                    ResourceAccessRules.Writes(texture.InitialAccess));
        }
        if (aliasActivated) return result;
        if (resource.EntryBoundaryStates is null) return result;
        foreach (TextureBoundaryState endpoint in resource.EntryBoundaryStates)
        {
            foreach (int cell in TextureCells(resource, endpoint.Range))
            {
                if (!_textureAssignedScratch[cell])
                {
                    result[cell] = new TexturePhysicalState(
                        endpoint.Sync,
                        endpoint.Access,
                        endpoint.Layout,
                        endpoint.Queue,
                        -1,
                        ResourceAccessRules.Writes(endpoint.Access));
                    _textureAssignedScratch[cell] = true;
                    continue;
                }

                TexturePhysicalState current = result[cell];
                if (current.Layout != endpoint.Layout)
                    throw new InvalidOperationException(
                        "Multiple initial Texture readers of one subresource must use the same layout.");
                result[cell] = new TexturePhysicalState(
                    current.Sync | endpoint.Sync,
                    current.Access | endpoint.Access,
                    current.Layout,
                    ReferenceEquals(current.Queue, endpoint.Queue) ? current.Queue : null,
                    -1,
                    current.Writes || ResourceAccessRules.Writes(endpoint.Access));
            }
        }
        return result;
    }

    private void ResolveInitialBufferTransfers(
        BufferBoundaryState[]? boundaryStates,
        Buffer buffer,
        Queue targetQueue,
        int pass,
        PipelineSync targetSync,
        ResourceAccess targetAccess)
    {
        if (boundaryStates is null) return;
        _transferSources.Clear();
        _transferTypes.Clear();
        foreach (BufferBoundaryState endpoint in boundaryStates)
        {
            Queue? source = endpoint.Queue;
            if (source is null || ReferenceEquals(source, targetQueue) ||
                source.Type == targetQueue.Type || !_transferSources.Add(source))
                continue;
            _lateReleases.Add(new PendingQueueRelease(
                source,
                targetQueue,
                pass,
                buffer,
                null,
                endpoint.Sync,
                endpoint.Access,
                null));
            if (_transferTypes.Add(source.Type))
            {
                _acquires[pass].Add(new QueueAcquire(
                    buffer,
                    null,
                    source.Type,
                    targetSync,
                    targetAccess,
                    null));
            }
        }
    }

    private void ResolveInitialTextureTransfers(
        TextureBoundaryState[]? boundaryStates,
        Texture texture,
        in TextureSubresourceRange range,
        Queue targetQueue,
        int pass,
        PipelineSync targetSync,
        ResourceAccess targetAccess,
        TextureLayout targetLayout)
    {
        if (boundaryStates is null) return;
        _transferSources.Clear();
        _transferTypes.Clear();
        foreach (TextureBoundaryState endpoint in boundaryStates)
        {
            if (!Overlaps(endpoint.Range, range)) continue;
            Queue? source = endpoint.Queue;
            if (source is null || ReferenceEquals(source, targetQueue) ||
                source.Type == targetQueue.Type || !_transferSources.Add(source))
                continue;
            _lateReleases.Add(new PendingQueueRelease(
                source,
                targetQueue,
                pass,
                texture,
                range,
                endpoint.Sync,
                endpoint.Access,
                endpoint.Layout));
            if (_transferTypes.Add(source.Type))
            {
                _acquires[pass].Add(new QueueAcquire(
                    texture,
                    range,
                    source.Type,
                    targetSync,
                    targetAccess,
                    targetLayout));
            }
        }
    }

    private void AddInitialCompletions(
        BufferBoundaryState[]? boundaryStates,
        Queue targetQueue,
        int pass)
    {
        if (boundaryStates is null) return;
        foreach (BufferBoundaryState endpoint in boundaryStates)
            if (endpoint.ReadyAfter.HasValue &&
                !ReferenceEquals(endpoint.ReadyAfter.Value.Queue, targetQueue))
                AddCompletion(_completionWaits[pass], endpoint.ReadyAfter.Value);
    }

    private void AddInitialCompletions(
        TextureBoundaryState[]? boundaryStates,
        Queue targetQueue,
        int pass)
    {
        if (boundaryStates is null) return;
        foreach (TextureBoundaryState endpoint in boundaryStates)
            if (endpoint.ReadyAfter.HasValue &&
                !ReferenceEquals(endpoint.ReadyAfter.Value.Queue, targetQueue))
                AddCompletion(_completionWaits[pass], endpoint.ReadyAfter.Value);
    }

    private static ResourceAccess MergeBufferAccess(ResourceAccess current, ResourceAccess next)
    {
        ResourceAccess currentWrites = current & WriteAccessMask;
        ResourceAccess nextWrites = next & WriteAccessMask;
        if (currentWrites != ResourceAccess.NoAccess && nextWrites != ResourceAccess.NoAccess &&
            currentWrites != nextWrites)
        {
            throw new NotSupportedException(
                "The current RHI cannot represent two different Buffer write classes in one stable pass state.");
        }
        return current | next;
    }

    private static TextureSubresourceRange TextureCellRange(
        in FrameTexture texture,
        int cell)
    {
        int mipCount = checked((int)texture.MipLevelCount);
        int layerCount = checked((int)texture.ArrayLayerCount);
        int mip = cell % mipCount;
        int layerAndAspect = cell / mipCount;
        int layer = layerAndAspect % layerCount;
        int aspect = layerAndAspect / layerCount;
        return new TextureSubresourceRange(
            checked((uint)mip),
            1,
            checked((uint)layer),
            1,
            TextureAspectAt(texture.Format, aspect));
    }

    private static void PrepareLists<T>(ref List<T>[] lists, int count)
    {
        int previousCount = lists.Length;
        if (previousCount < count)
            Array.Resize(ref lists, count);

        for (int i = 0; i < count; i++)
        {
            if (i >= previousCount || lists[i] is null)
                lists[i] = [];
            else
                lists[i].Clear();
        }
    }

    private static void AddCompletion(List<QueueCompletion> values, QueueCompletion completion)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (!ReferenceEquals(values[i].Queue, completion.Queue)) continue;
            if (completion.Value > values[i].Value) values[i] = completion;
            return;
        }
        values.Add(completion);
    }

    private static bool Overlaps(in QueryRange left, in QueryRange right)
    {
        uint leftEnd = checked(left.FirstQuery + left.QueryCount);
        uint rightEnd = checked(right.FirstQuery + right.QueryCount);
        return left.FirstQuery < rightEnd && right.FirstQuery < leftEnd;
    }

    private const ResourceAccess WriteAccessMask =
        ResourceAccess.RenderTarget |
        ResourceAccess.UnorderedAccess |
        ResourceAccess.DepthStencilWrite |
        ResourceAccess.StreamOutput |
        ResourceAccess.CopyDestination |
        ResourceAccess.ResolveDestination |
        ResourceAccess.RayTracingAccelerationStructureWrite;

    private readonly record struct BufferPhysicalState(
        PipelineSync Sync,
        ResourceAccess Access,
        Queue? Queue,
        int LastPass,
        bool Writes);

    private readonly record struct TexturePhysicalState(
        PipelineSync Sync,
        ResourceAccess Access,
        TextureLayout Layout,
        Queue? Queue,
        int LastPass,
        bool Writes);

    private readonly record struct PendingQueueRelease(
        Queue SourceQueue,
        Queue DestinationQueue,
        int ConsumerPass,
        Resource Resource,
        TextureSubresourceRange? TextureRange,
        PipelineSync Sync,
        ResourceAccess Access,
        TextureLayout? Layout);

}

