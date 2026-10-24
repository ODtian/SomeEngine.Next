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
    private List<int>[] _physicalPredecessors = [];
    private readonly List<PendingQueueRelease> _lateReleases = [];
    private readonly HashSet<Queue> _transferSources =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<QueueType> _transferTypes = [];
    private readonly List<PendingBarrierTransition> _pendingBarrierTransitions = [];
    private readonly List<TextureRegionState> _textureRegions = [];

    private void ResolveQuerySynchronization(int poolIndex)
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
            DevelopmentCheck.Require(queue.Type == pool.Resource.Description.QueueType,
                $"QueryPool '{pool.Resource.Label}' requires a {pool.Resource.Description.QueueType} Queue.");
            _collectSynchronization.WaitPasses.Add(pass);
        }
    }

    private void ResolveShaderTableSynchronization(int tableIndex)
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
            _collectSynchronization.WaitPasses.Add(pass);
        }
    }

    private void ResolveAliasingBarrier((GraphAccessTargetKind Kind, int Index) key)
    {
        if (key.Kind == GraphAccessTargetKind.Buffer)
        {
            FrameBuffer row = _buffers[key.Index];
            if (row.LastPass >= 0 && row.Placement.Aliases)
            {
                if (row.Placement.AliasingPredecessor is { } previous) AddAliasingBoundary(previous, row.Resource!, row.FirstPass);
                else Synchronization(row.FirstPass).AliasAfter.Add(new(row.Resource!));
            }
        }
        else if (key.Kind == GraphAccessTargetKind.Texture)
        {
            FrameTexture row = _textures[key.Index];
            if (row.LastPass >= 0 && row.Placement.Aliases)
            {
                if (row.Placement.AliasingPredecessor is { } previous) AddAliasingBoundary(previous, row.Resource!, row.FirstPass);
                else Synchronization(row.FirstPass).AliasAfter.Add(new(row.Resource!));
            }
        }
    }

    private void AddAliasingBoundary(
        Resource before,
        Resource after,
        int currentPass)
    {
        int previousPass = FindAliasingPredecessorLastPass(
            before,
            currentPass);
        AliasingResource beforeResource = new(before);
        AliasingResource afterResource = new(after);
        if (previousPass >= 0)
        {
            AddUnique(Synchronization(currentPass).Predecessors, previousPass);
            _pendingBarrierTransitions.Add(PendingBarrierTransition.ForAliasing(
                previousPass,
                currentPass,
                _executionOrder.Position(currentPass),
                beforeResource,
                afterResource));
            return;
        }
        Synchronization(currentPass).AliasBefore.Add(beforeResource);
        Synchronization(currentPass).AliasAfter.Add(afterResource);
    }

    private int FindAliasingPredecessorLastPass(
        Resource predecessor,
        int currentPass)
    {
        if (!_resourceRows.TryGetValue(predecessor, out var key) || !ResourceExists(key)) return -1;
        int pass = key.Kind == GraphAccessTargetKind.Buffer ? _buffers[key.Index].LastPass : _textures[key.Index].LastPass;
        return pass >= 0 && _executionOrder.Position(pass) < _executionOrder.Position(currentPass) &&
            ReferenceEquals(_passes[pass].Queue, _passes[currentPass].Queue) ? pass : -1;
    }

    private void ResolveBufferSynchronization(int bufferIndex)
    {

        List<int> accesses = CollectResourceAccesses(GraphAccessTargetKind.Buffer, bufferIndex, enabledOnly: false);
        FilterLiveAccesses(accesses);
        if (accesses.Count == 0) return;
        SortAccessesBySchedule(accesses);

        FrameBuffer resource = _buffers[bufferIndex];
        if (resource.Lifetime == RenderGraphResourceLifetime.Persistent && resource.Definition is not null)
            resource.EntryBoundaryStates = resource.Definition.BoundaryStates;
        Buffer buffer = resource.Resource!;
        DevelopmentCheck.Require(buffer is not null, "A live Buffer must be materialized.");
        if (!resource.Placement.Aliases) ResumeBufferTransfers(resource, buffer);
        if (buffer.Device.Capabilities.SupportsConcurrentBufferReads)
        {
            ResolveConcurrentBufferSynchronization(resource, buffer, accesses);
            return;
        }
        bool aliasActivated = resource.Placement.Aliases;
        BufferPhysicalState state = aliasActivated
            ? new BufferPhysicalState(
                PipelineSync.All,
                ResourceAccess.NoAccess,
                null,
                -1,
                false,
                false)
            : InitialBufferState(resource, buffer);

        int cursor = 0;
        while (cursor < accesses.Count)
        {
            int pass = _accesses[accesses[cursor]].PassIndex;
            PipelineSync targetSync = PipelineSync.None;
            ResourceAccess targetAccess = ResourceAccess.NoAccess;
            bool targetWrites = false;
            bool targetMergeableWrites = true;
            int end = cursor;
            while (end < accesses.Count && _accesses[accesses[end]].PassIndex == pass)
            {
                FrameResourceAccess access = _accesses[accesses[end]];
                targetSync |= access.Sync;
                targetAccess = MergeBufferAccess(targetAccess, access.Access);
                targetWrites |= access.Mode != GraphAccessMode.Read;
                targetMergeableWrites &=
                    access.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered;
                end++;
            }
            targetMergeableWrites &= targetWrites;

            Queue targetQueue = _passes[pass].Queue!;
            bool initialQueueTransfer = false;
            if (state.LastPass < 0)
            {
                if (!aliasActivated)
                {
                    AddInitialCompletions(resource.EntryBoundaryStates, targetQueue, pass);
                    initialQueueTransfer = ResolveInitialBufferTransfers(
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
            bool mergeableWriteChain =
                state.MergeableWrites &&
                targetMergeableWrites &&
                !queueChanged &&
                state.Access == targetAccess;
            bool barrierFreeInitialAccess =
                state.LastPass < 0 &&
                resource.Definition is null &&
                resource.EntryBoundaryStates is not { Length: > 0 } &&
                !resource.Placement.Aliases &&
                state.Sync == PipelineSync.None &&
                state.Access == ResourceAccess.NoAccess &&
                !RequiresFutureBufferBarrier(
                    accesses,
                    end,
                    targetSync,
                    targetAccess,
                    targetQueue,
                    targetWrites);
            if (queueChanged)
            {
                if (state.LastPass >= 0)
                {
                    AddUnique(Synchronization(pass).Predecessors, state.LastPass);
                    if (state.Queue!.Type != targetQueue.Type)
                    {
                        Synchronization(state.LastPass).Releases.Add(new QueueRelease(
                            buffer, null, state.Sync, state.Access, null, targetQueue.Type));
                        Synchronization(pass).Acquires.Add(new QueueAcquire(
                            buffer, null, state.Queue.Type, targetSync, targetAccess, null));
                    }
                }
                else if (state.Queue is not null && state.Queue.Type != targetQueue.Type)
                {
                    _collectSynchronization.LateReleases.Add(new PendingQueueRelease(
                        state.Queue,
                        targetQueue,
                        pass,
                        buffer,
                        null,
                        state.Sync,
                        state.Access,
                        null));
                    Synchronization(pass).Acquires.Add(new QueueAcquire(
                        buffer, null, state.Queue.Type, targetSync, targetAccess, null));
                }
            }

            if (!initialQueueTransfer &&
                (!queueChanged || state.Queue?.Type == targetQueue.Type))
            {
                if (!mergeableWriteChain &&
                    !barrierFreeInitialAccess &&
                    (stateChanged || ordering))
                {
                    AddBufferTransition(
                        state.LastPass,
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
                mergeableWriteChain ? state.Sync | targetSync : targetSync,
                targetAccess,
                targetQueue,
                pass,
                targetWrites,
                targetMergeableWrites);
            cursor = end;
        }
    }

    private readonly Dictionary<Queue, BufferPhysicalState> _bufferQueueStates =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _bufferReaders = [];

    private void ResolveConcurrentBufferSynchronization(FrameBuffer resource, Buffer buffer, List<int> accesses)
    {
        _bufferQueueStates.Clear();
        _bufferReaders.Clear();
        int writer = -1;
        int position = 0;
        while (position < accesses.Count)
        {
            int pass = _accesses[accesses[position]].PassIndex;
            PipelineSync sync = PipelineSync.None;
            ResourceAccess access = ResourceAccess.NoAccess;
            bool writes = false;
            bool mergeableWrites = true;
            do
            {
                FrameResourceAccess row = _accesses[accesses[position++]];
                sync |= row.Sync;
                access = MergeBufferAccess(access, row.Access);
                writes |= row.Mode != GraphAccessMode.Read;
                mergeableWrites &= row.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered;
            } while (position < accesses.Count && _accesses[accesses[position]].PassIndex == pass);
            Queue queue = _passes[pass].Queue!;
            bool seen = _bufferQueueStates.TryGetValue(queue, out BufferPhysicalState previous);
            bool mergeReads = seen && !previous.Writes && !writes &&
                !ResourceAccessRules.Writes(previous.Access | access);
            if (!writes && !mergeReads && !ResourceAccessRules.Writes(access))
                IncludeFollowingBufferReads(accesses, position, queue, ref sync, ref access);
            if (!seen && !resource.Placement.Aliases)
                AddInitialCompletions(resource.EntryBoundaryStates, queue, pass);
            if (!seen && resource.Placement.Aliases)
            {
                // The alias barrier leaves this buffer at NoAccess. Activate
                // its first declared access with an explicit transition.
                AddBufferTransition(-1, pass, buffer,
                    pass == resource.FirstPass ? PipelineSync.All : PipelineSync.None, sync,
                    ResourceAccess.NoAccess, access, false);
            }

            // D3D12 permits concurrent readers. Serialize writers for the whole buffer:
            // even disjoint writes may race through cache lines shared by different queues.
            if (writer >= 0 && !ReferenceEquals(_passes[writer].Queue, queue))
                AddUnique(Synchronization(pass).Predecessors, writer);
            if (writes)
            {
                foreach (int reader in _bufferReaders)
                    if (!ReferenceEquals(_passes[reader].Queue, queue))
                        AddUnique(Synchronization(pass).Predecessors, reader);
                _bufferReaders.Clear();
                writer = pass;
            }
            else AddUnique(_bufferReaders, pass);

            bool mergeWrites = seen && previous.MergeableWrites && mergeableWrites && writes && previous.Access == access;
            if (seen && !mergeWrites && !mergeReads &&
                (previous.Writes || writes || previous.Sync != sync || previous.Access != access))
                AddBufferTransition(previous.LastPass, pass, buffer, previous.Sync, sync,
                    previous.Access, access, false);
            _bufferQueueStates[queue] = new BufferPhysicalState(
                mergeWrites || mergeReads ? previous.Sync | sync : sync,
                mergeReads ? MergeBufferAccess(previous.Access, access) : access,
                queue, pass, writes, mergeableWrites && writes);
        }
    }

    private void IncludeFollowingBufferReads(List<int> accesses, int position, Queue queue,
        ref PipelineSync sync, ref ResourceAccess access)
    {
        // The write-to-read barrier must make the write visible to every reader,
        // including later shader stages, before read-to-read barriers can be omitted.
        for (int index = position; index < accesses.Count; index++)
        {
            FrameResourceAccess next = _accesses[accesses[index]];
            if (next.Mode != GraphAccessMode.Read || ResourceAccessRules.Writes(next.Access)) break;
            if (!ReferenceEquals(_passes[next.PassIndex].Queue, queue)) continue;
            sync |= next.Sync;
            access = MergeBufferAccess(access, next.Access);
        }
    }

    private bool RequiresFutureBufferBarrier(
        List<int> accesses,
        int cursor,
        PipelineSync previousSync,
        ResourceAccess previousAccess,
        Queue previousQueue,
        bool previousWrites)
    {
        while (cursor < accesses.Count)
        {
            int pass = _accesses[accesses[cursor]].PassIndex;
            PipelineSync nextSync = PipelineSync.None;
            ResourceAccess nextAccess = ResourceAccess.NoAccess;
            bool nextWrites = false;
            int end = cursor;
            while (end < accesses.Count && _accesses[accesses[end]].PassIndex == pass)
            {
                FrameResourceAccess access = _accesses[accesses[end]];
                nextSync |= access.Sync;
                nextAccess = MergeBufferAccess(nextAccess, access.Access);
                nextWrites |= access.Mode != GraphAccessMode.Read;
                end++;
            }

            Queue nextQueue = _passes[pass].Queue!;
            bool queueChanged = !ReferenceEquals(previousQueue, nextQueue);
            if ((queueChanged && previousQueue.Type != nextQueue.Type) ||
                previousSync != nextSync ||
                previousAccess != nextAccess ||
                previousWrites ||
                nextWrites)
            {
                return true;
            }

            previousSync = nextSync;
            previousAccess = nextAccess;
            previousQueue = nextQueue;
            previousWrites = nextWrites;
            cursor = end;
        }
        return false;
    }

    private void ResolveTextureSynchronization(int textureIndex)
    {

        List<int> accesses = CollectResourceAccesses(GraphAccessTargetKind.Texture, textureIndex, enabledOnly: false);
        FilterLiveAccesses(accesses);
        if (accesses.Count == 0) return;
        SortAccessesBySchedule(accesses);

        FrameTexture resource = _textures[textureIndex];
        if (resource.Lifetime == RenderGraphResourceLifetime.Persistent && resource.Definition is not null)
            resource.EntryBoundaryStates = resource.Definition.BoundaryStates;
        Texture texture = resource.Resource!;
        DevelopmentCheck.Require(texture is not null, "A live Texture must be materialized.");
        if (!resource.Placement.Aliases) ResumeTextureTransfers(resource, texture);
        bool aliasActivated = resource.Placement.Aliases;
        InitialTextureStates(resource, texture, aliasActivated);

        for (int first = 0; first < accesses.Count;)
        {
            int pass = _accesses[accesses[first]].PassIndex;
            int end = first + 1;
            while (end < accesses.Count && _accesses[accesses[end]].PassIndex == pass) end++;
            Queue targetQueue = _passes[pass].Queue!;
            // Align regions to every declaration in this pass before computing its
            // single incoming state. Declarations do not execute between barriers.
            for (int item = first; item < end; item++)
            {
                int count = _textureRegions.Count;
                TextureSubresourceRange range = _accesses[accesses[item]].TextureRange;
                for (int regionIndex = 0; regionIndex < count; regionIndex++)
                    SplitTextureRegion(regionIndex, range);
            }
            for (int regionIndex = 0; regionIndex < _textureRegions.Count; regionIndex++)
            {
                TextureRegionState region = _textureRegions[regionIndex];
                FrameResourceAccess access = default;
                bool found = false;
                bool targetWrites = false;
                bool targetMergeableWrites = true;
                for (int item = first; item < end; item++)
                {
                    FrameResourceAccess source = _accesses[accesses[item]];
                    if (!Overlaps(region.Range, source.TextureRange)) continue;
                    if (!found) access = source;
                    else
                    {
                        if (access.TextureLayout != source.TextureLayout)
                            throw new InvalidOperationException(
                                $"Pass '{_passes[pass].Label}' declares conflicting layouts for Texture '{texture.Label}'.");
                        access.Sync |= source.Sync;
                        access.Access |= source.Access;
                    }
                    found = true;
                    targetWrites |= source.Mode != GraphAccessMode.Read;
                    targetMergeableWrites &= source.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered;
                }
                if (!found) continue;
                targetMergeableWrites &= targetWrites;
                TexturePhysicalState state = region.State;
                TextureSubresourceRange cellRange = region.Range;
                bool initialQueueTransfer = false;
                if (state.LastPass < 0)
                {
                    if (!aliasActivated)
                    {
                        AddInitialCompletions(resource.EntryBoundaryStates, targetQueue, pass);
                        initialQueueTransfer = ResolveInitialTextureTransfers(
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
                bool mergeableWriteChain =
                    state.MergeableWrites &&
                    targetMergeableWrites &&
                    !queueChanged &&
                    state.Access == access.Access &&
                    state.Layout == access.TextureLayout;
                bool hoistableInitialTransition =
                    state.LastPass < 0 &&
                    resource.EntryBoundaryStates is not { Length: > 0 } &&
                    !resource.Placement.Aliases;
                if (queueChanged)
                {
                    if (state.LastPass >= 0)
                    {
                        AddUnique(Synchronization(pass).Predecessors, state.LastPass);
                        if (state.Queue!.Type != targetQueue.Type)
                        {
                            AddTextureQueueTransfer(state.LastPass, pass, texture, cellRange,
                                state.Queue, targetQueue, state.Sync, access.Sync,
                                state.Access, access.Access, state.Layout, access.TextureLayout);
                        }
                    }
                    else if (state.Queue is not null && state.Queue.Type != targetQueue.Type)
                    {
                        AddTextureQueueTransfer(-1, pass, texture, cellRange,
                            state.Queue, targetQueue, state.Sync, access.Sync,
                            state.Access, access.Access, state.Layout, access.TextureLayout);
                    }
                }

                if (!initialQueueTransfer &&
                    (!queueChanged || state.Queue?.Type == targetQueue.Type))
                {
                    if (!mergeableWriteChain && (stateChanged || ordering))
                    {
                        AddTextureTransition(
                            state.LastPass,
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

                _textureRegions[regionIndex] = region with { State = new TexturePhysicalState(
                    mergeableWriteChain ? state.Sync | access.Sync : access.Sync,
                    access.Access,
                    access.TextureLayout,
                    targetQueue,
                    pass,
                    targetWrites,
                    targetMergeableWrites) };
            }
            first = end;
        }

        Queue? presentQueue = null;
        foreach ((GraphIdentity identity, _, Queue queue) in _frame.SwapchainImages)
        {
            if (identity != resource.Identity) continue;
            presentQueue = queue;
            break;
        }
        if (presentQueue is null) return;

        foreach (TextureRegionState region in _textureRegions)
        {
            TexturePhysicalState state = region.State;
            if (state.LastPass < 0)
            {
                DevelopmentCheck.Require(state.Layout == TextureLayout.Present &&
                    state.Sync == PipelineSync.None && state.Access == ResourceAccess.NoAccess,
                    "A submitted SwapchainImage must be written or explicitly returned to Present before submission.");
                continue;
            }
            DevelopmentCheck.Require(ReferenceEquals(state.Queue, presentQueue),
                "The final SwapchainImage use must execute on its presentation Queue.");
            if (state.Sync == PipelineSync.None &&
                state.Access == ResourceAccess.NoAccess &&
                state.Layout == TextureLayout.Present)
                continue;

            Synchronization(state.LastPass).AfterTextures.Add(new TextureBarrier(
                texture,
                region.Range,
                state.Sync,
                PipelineSync.None,
                state.Access,
                ResourceAccess.NoAccess,
                state.Layout,
                TextureLayout.Present));
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
                _executionOrder.Position(currentPass),
                barrier));
            return;
        }
        Synchronization(currentPass).BeforeBuffers.Add(barrier);
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
            Synchronization(previousPass).AfterTextures.Add(new TextureBarrier(
                texture, range, beforeSync, afterSync, beforeAccess, afterAccess,
                beforeLayout, afterLayout, BarrierPhase.Begin));
            Synchronization(currentPass).BeforeTextures.Add(new TextureBarrier(
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
                    _executionOrder.Position(currentPass),
                    barrier));
                return;
            }
            int placementPass = hoistableInitialTransition
                ? FindFirstQueuePass(currentPass)
                : currentPass;
            Synchronization(placementPass).BeforeTextures.Add(barrier);
        }
    }

    private void PlacePendingBarrierTransitions()
    {
        _pendingBarrierTransitions.Sort(static (left, right) =>
        {
            int byEnd = left.CurrentScheduledOrdinal.CompareTo(
                right.CurrentScheduledOrdinal);
            if (byEnd != 0) return byEnd;
            int byPrevious = left.PreviousPass.CompareTo(right.PreviousPass);
            if (byPrevious != 0) return byPrevious;
            int byLayer = left.Texture.Range.FirstArrayLayer.CompareTo(right.Texture.Range.FirstArrayLayer);
            return byLayer != 0 ? byLayer :
                left.Texture.Range.FirstMipLevel.CompareTo(right.Texture.Range.FirstMipLevel);
        });
        foreach (PendingBarrierTransition pending in _pendingBarrierTransitions)
        {
            int placementPass = FindExistingBarrierBoundary(
                pending.PreviousPass,
                pending.CurrentPass);
            switch (pending.Kind)
            {
                case PendingBarrierKind.Buffer:
                    Synchronization(placementPass).BeforeBuffers.Add(pending.Buffer);
                    break;
                case PendingBarrierKind.Texture:
                    Synchronization(placementPass).BeforeTextures.Add(pending.Texture);
                    break;
                case PendingBarrierKind.Aliasing:
                    Synchronization(placementPass).AliasBefore.Add(pending.AliasBefore);
                    Synchronization(placementPass).AliasAfter.Add(pending.AliasAfter);
                    break;
                default:
                    DevelopmentCheck.Fail("Unknown barrier transition kind.");
                    break;
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
        long CurrentScheduledOrdinal,
        PendingBarrierKind Kind,
        BufferBarrier Buffer,
        TextureBarrier Texture,
        AliasingResource AliasBefore,
        AliasingResource AliasAfter)
    {
        internal static PendingBarrierTransition ForBuffer(
            int previousPass,
            int currentPass,
            long currentScheduledOrdinal,
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
            long currentScheduledOrdinal,
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
            long currentScheduledOrdinal,
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
                false,
                false);
        PipelineSync sync = PipelineSync.None;
        ResourceAccess access = ResourceAccess.NoAccess;
        Queue? queue = null;
        foreach (BufferBoundaryState endpoint in resource.EntryBoundaryStates)
        {
            sync |= endpoint.Sync;
            access = MergeBufferAccess(access, endpoint.Access);
            Queue? endpointQueue = endpoint.ReleasedTo ?? endpoint.Queue;
            if (queue is null) queue = endpointQueue;
            else if (endpointQueue is not null && !ReferenceEquals(queue, endpointQueue)) queue = null;
        }
        return new BufferPhysicalState(
            sync, access, queue, -1, ResourceAccessRules.Writes(access), false);
    }

    private void InitialTextureStates(
        in FrameTexture resource,
        Texture texture,
        bool aliasActivated)
    {
        _textureRegions.Clear();
        TexturePhysicalState initial = aliasActivated
            ? new(PipelineSync.None, ResourceAccess.NoAccess, TextureLayout.Undefined, null, -1, false, false)
            : new(texture.InitialSync, texture.InitialAccess, texture.InitialLayout, null, -1,
                ResourceAccessRules.Writes(texture.InitialAccess), false);
        for (int aspect = 0; aspect < TextureAspectCount(resource.Format); aspect++)
            _textureRegions.Add(new TextureRegionState(
                new TextureSubresourceRange(0, resource.MipLevelCount, 0, resource.ArrayLayerCount,
                    TextureAspectAt(resource.Format, aspect)), initial, false));
        if (aliasActivated || resource.EntryBoundaryStates is null) return;
        foreach (TextureBoundaryState endpoint in resource.EntryBoundaryStates)
        {
            int regionCount = _textureRegions.Count;
            for (int index = 0; index < regionCount; index++)
            {
                if (!SplitTextureRegion(index, endpoint.Range)) continue;
                TextureRegionState region = _textureRegions[index];
                TexturePhysicalState current = region.State;
                DevelopmentCheck.Require(!region.Assigned || current.Layout == endpoint.Layout,
                    "Multiple initial Texture readers of one subresource must use the same layout.");
                TexturePhysicalState state = region.Assigned
                    ? new(current.Sync | endpoint.Sync, current.Access | endpoint.Access, current.Layout,
                        ReferenceEquals(current.Queue, endpoint.ReleasedTo ?? endpoint.Queue) ? current.Queue : null, -1,
                        current.Writes || ResourceAccessRules.Writes(endpoint.Access), false)
                    : new(endpoint.Sync, endpoint.Access, endpoint.Layout, endpoint.ReleasedTo ?? endpoint.Queue, -1,
                        ResourceAccessRules.Writes(endpoint.Access), false);
                _textureRegions[index] = region with { State = state, Assigned = true };
            }
        }
    }

    // Keep disjoint rectangles. A use introduces only its intersecting boundaries;
    // untouched mip/layer spans retain both their state and their extent.
    private bool SplitTextureRegion(int index, in TextureSubresourceRange selected)
    {
        TextureRegionState region = _textureRegions[index];
        TextureSubresourceRange source = region.Range;
        if (!Overlaps(source, selected)) return false;
        uint firstMip = Math.Max(source.FirstMipLevel, selected.FirstMipLevel);
        uint mipEnd = Math.Min(source.FirstMipLevel + source.MipLevelCount,
            selected.FirstMipLevel + selected.MipLevelCount);
        uint firstLayer = Math.Max(source.FirstArrayLayer, selected.FirstArrayLayer);
        uint layerEnd = Math.Min(source.FirstArrayLayer + source.ArrayLayerCount,
            selected.FirstArrayLayer + selected.ArrayLayerCount);
        AddRemainder(source.FirstMipLevel, firstMip, source.FirstArrayLayer,
            source.FirstArrayLayer + source.ArrayLayerCount);
        AddRemainder(mipEnd, source.FirstMipLevel + source.MipLevelCount,
            source.FirstArrayLayer, source.FirstArrayLayer + source.ArrayLayerCount);
        AddRemainder(firstMip, mipEnd, source.FirstArrayLayer, firstLayer);
        AddRemainder(firstMip, mipEnd, layerEnd, source.FirstArrayLayer + source.ArrayLayerCount);
        _textureRegions[index] = region with { Range = new TextureSubresourceRange(
            firstMip, mipEnd - firstMip, firstLayer, layerEnd - firstLayer, source.Aspects) };
        return true;

        void AddRemainder(uint mip, uint endMip, uint layer, uint endLayer)
        {
            if (mip == endMip || layer == endLayer) return;
            _textureRegions.Add(region with { Range = new TextureSubresourceRange(
                mip, endMip - mip, layer, endLayer - layer, source.Aspects) });
        }
    }

    private readonly record struct TextureRegionState(
        TextureSubresourceRange Range, TexturePhysicalState State, bool Assigned);

    private bool ResolveInitialBufferTransfers(
        BufferBoundaryState[]? boundaryStates,
        Buffer buffer,
        Queue targetQueue,
        int pass,
        PipelineSync targetSync,
        ResourceAccess targetAccess)
    {
        if (boundaryStates is null) return false;
        bool transferred = false;
        _transferSources.Clear();
        _transferTypes.Clear();
        foreach (BufferBoundaryState endpoint in boundaryStates)
        {
            Queue? source = endpoint.ReleasedTo ?? endpoint.Queue;
            if (source is null || ReferenceEquals(source, targetQueue) ||
                source.Type == targetQueue.Type || !_transferSources.Add(source))
                continue;
            _collectSynchronization.LateReleases.Add(new PendingQueueRelease(
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
                DevelopmentCheck.Require(_transferTypes.Count == 1,
                    $"Buffer '{buffer.Label}' cannot have overlapping entry ownership on multiple Queue types.");
                transferred = true;
                Synchronization(pass).Acquires.Add(new QueueAcquire(
                    buffer,
                    null,
                    source.Type,
                    targetSync,
                    targetAccess,
                    null));
            }
        }
        return transferred;
    }

    private bool ResolveInitialTextureTransfers(
        TextureBoundaryState[]? boundaryStates,
        Texture texture,
        in TextureSubresourceRange range,
        Queue targetQueue,
        int pass,
        PipelineSync targetSync,
        ResourceAccess targetAccess,
        TextureLayout targetLayout)
    {
        if (boundaryStates is null) return false;
        bool transferred = false;
        _transferSources.Clear();
        _transferTypes.Clear();
        foreach (TextureBoundaryState endpoint in boundaryStates)
        {
            if (!Overlaps(endpoint.Range, range)) continue;
            Queue? source = endpoint.ReleasedTo ?? endpoint.Queue;
            if (source is null || ReferenceEquals(source, targetQueue) ||
                source.Type == targetQueue.Type || !_transferSources.Add(source))
                continue;
            if (_transferTypes.Add(source.Type))
            {
                transferred = true;
                AddTextureQueueTransfer(-1, pass, texture, range, source, targetQueue,
                    endpoint.Sync, targetSync, endpoint.Access, targetAccess, endpoint.Layout, targetLayout);
            }
        }
        return transferred;
    }

    private void AddInitialCompletions(BufferBoundaryState[]? states, Queue queue, int pass) =>
        _collectSynchronization.WaitPasses.Add(pass);

    private void AddInitialCompletions(TextureBoundaryState[]? states, Queue queue, int pass) =>
        _collectSynchronization.WaitPasses.Add(pass);

    private static ResourceAccess MergeBufferAccess(ResourceAccess current, ResourceAccess next)
    {
        DevelopmentCheck.Require((current & WriteAccessMask) == ResourceAccess.NoAccess ||
            (next & WriteAccessMask) == ResourceAccess.NoAccess ||
            (current & WriteAccessMask) == (next & WriteAccessMask),
            "A Buffer pass state cannot combine different write classes.");
        return current | next;
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
        bool Writes,
        bool MergeableWrites);

    private readonly record struct TexturePhysicalState(
        PipelineSync Sync,
        ResourceAccess Access,
        TextureLayout Layout,
        Queue? Queue,
        int LastPass,
        bool Writes,
        bool MergeableWrites);

    private readonly record struct PendingQueueRelease(
        Queue SourceQueue,
        Queue DestinationQueue,
        int ConsumerPass,
        Resource Resource,
        TextureSubresourceRange? TextureRange,
        PipelineSync Sync,
        ResourceAccess Access,
        TextureLayout? Layout,
        QueueType? AcquireFrom = null,
        bool Release = true);

    private void ResumeBufferTransfers(in FrameBuffer resource, Buffer buffer)
    {
        foreach (BufferBoundaryState state in resource.EntryBoundaryStates ?? [])
        {
            if (state.ReleasedTo is null) continue;
            _collectSynchronization.LateReleases.Add(new PendingQueueRelease(state.ReleasedTo, state.ReleasedTo,
                -1, buffer, null, PipelineSync.None, ResourceAccess.NoAccess, null,
                state.Queue!.Type, Release: false));
            return; // Buffer ownership transfers cover the complete allocation.
        }
    }

    private readonly HashSet<TextureSubresourceRange> _resumedTextureRanges = [];

    private void ResumeTextureTransfers(in FrameTexture resource, Texture texture)
    {
        _resumedTextureRanges.Clear();
        foreach (TextureBoundaryState state in resource.EntryBoundaryStates ?? [])
        {
            if (state.ReleasedTo is null) continue;
            DevelopmentCheck.Require(state.ReleaseRange.HasValue,
                "A released texture must retain the complete ownership-transfer range.");
            TextureSubresourceRange range = state.ReleaseRange!.Value;
            if (!_resumedTextureRanges.Add(range)) continue;
            _collectSynchronization.LateReleases.Add(new PendingQueueRelease(state.ReleasedTo, state.ReleasedTo,
                -1, texture, range, PipelineSync.None, ResourceAccess.NoAccess, TextureLayout.General,
                state.Queue!.Type, Release: false));
        }
    }

    private sealed class PassSynchronization
    {
        internal readonly List<BufferBarrier> BeforeBuffers = [], AfterBuffers = [];
        internal readonly List<TextureBarrier> BeforeTextures = [], AfterTextures = [];
        internal readonly List<QueueAcquire> Acquires = [];
        internal readonly List<QueueRelease> Releases = [];
        internal readonly List<AliasingResource> AliasBefore = [], AliasAfter = [];
        internal readonly List<int> Predecessors = [];

        internal void Clear()
        {
            BeforeBuffers.Clear(); AfterBuffers.Clear(); BeforeTextures.Clear(); AfterTextures.Clear();
            Acquires.Clear(); Releases.Clear(); AliasBefore.Clear(); AliasAfter.Clear(); Predecessors.Clear();
        }
    }

    private sealed class ResourceSynchronization
    {
        internal readonly Dictionary<int, PassSynchronization> Passes = [];
        internal readonly List<PendingQueueRelease> LateReleases = [];
        internal readonly HashSet<int> WaitPasses = [];
        internal readonly Stack<PassSynchronization> Reusable = [];

        internal void Clear()
        {
            foreach (PassSynchronization pass in Passes.Values)
            {
                pass.Clear();
                Reusable.Push(pass);
            }
            Passes.Clear(); LateReleases.Clear(); WaitPasses.Clear();
        }
    }

    private readonly Dictionary<(GraphAccessTargetKind Kind, int Index), ResourceSynchronization> _synchronizationByResource = [];
    private readonly Dictionary<int, HashSet<(GraphAccessTargetKind Kind, int Index)>> _synchronizationByPass = [];
    private readonly Dictionary<int, HashSet<(GraphAccessTargetKind Kind, int Index)>> _initialWaitResources = [];
    private readonly Dictionary<(GraphAccessTargetKind Kind, int Index), bool> _synchronizationChanges = [];
    private readonly HashSet<int> _changedSynchronizationPasses = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _changedBoundaryResources = [];
    private readonly List<int> _previousPhysicalPredecessors = [];
    private ResourceSynchronization _collectSynchronization = null!;
    private ResourceSynchronization _synchronizationScratch = new();
    private int _synchronizationPassCount;

    private PassSynchronization Synchronization(int pass)
    {
        if (!_collectSynchronization.Passes.TryGetValue(pass, out PassSynchronization? value))
        {
            value = _collectSynchronization.Reusable.TryPop(out PassSynchronization? reusable) ? reusable : new();
            _collectSynchronization.Passes.Add(pass, value);
        }
        return value;
    }

    private void ResolveSynchronization()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Synchronization);
        EnsureCapacity(ref _beforeBufferBarriers, _passes.Count);
        EnsureCapacity(ref _afterBufferBarriers, _passes.Count);
        EnsureCapacity(ref _beforeTextureBarriers, _passes.Count);
        EnsureCapacity(ref _afterTextureBarriers, _passes.Count);
        EnsureCapacity(ref _acquires, _passes.Count);
        EnsureCapacity(ref _releases, _passes.Count);
        EnsureCapacity(ref _aliasBeforeResources, _passes.Count);
        EnsureCapacity(ref _aliasAfterResources, _passes.Count);
        EnsureCapacity(ref _physicalPredecessors, _passes.Count);
        for (int pass = _synchronizationPassCount; pass < _passes.Count; pass++)
        {
            _beforeBufferBarriers[pass] ??= []; _afterBufferBarriers[pass] ??= [];
            _beforeTextureBarriers[pass] ??= []; _afterTextureBarriers[pass] ??= [];
            _acquires[pass] ??= []; _releases[pass] ??= [];
            _aliasBeforeResources[pass] ??= []; _aliasAfterResources[pass] ??= [];
            _physicalPredecessors[pass] ??= [];
        }
        _synchronizationPassCount = _passes.Count;
        foreach (var (key, bindingOnly) in _synchronizationChanges)
        {
            _stateUpdateResourcesChanged.Add(key);
            _synchronizationByResource.TryGetValue(key, out ResourceSynchronization? previous);
            if (bindingOnly && previous is not null && TryRebindSynchronization(key, previous)) continue;
            ResourceSynchronization value = _synchronizationScratch;
            value.Clear();
            bool exists = ResourceExists(key);
            if (exists)
            {
                _collectSynchronization = value;
                _pendingBarrierTransitions.Clear();
                _textureQueueTransfers.Clear();
                ResolveAliasingBarrier(key);
                switch (key.Kind)
                {
                    case GraphAccessTargetKind.Buffer: ResolveBufferSynchronization(key.Index); break;
                    case GraphAccessTargetKind.Texture: ResolveTextureSynchronization(key.Index); break;
                    case GraphAccessTargetKind.QueryPool: ResolveQuerySynchronization(key.Index); break;
                    case GraphAccessTargetKind.RayTracingShaderTable: ResolveShaderTableSynchronization(key.Index); break;
                }
                PlacePendingBarrierTransitions();
                CompactTextureBarriers();
            }
            // Derivation may reject a boundary. Do not detach the previous result until every
            // barrier and transfer for this resource has been constructed successfully.
            if (previous is not null)
            {
                foreach (int pass in previous.Passes.Keys)
                {
                    _changedSynchronizationPasses.Add(pass);
                    _synchronizationByPass[pass].Remove(key);
                }
                foreach (int pass in previous.WaitPasses) _initialWaitResources[pass].Remove(key);
                foreach (PendingQueueRelease release in previous.LateReleases) _lateReleases.Remove(release);
            }
            if (!exists)
            {
                _synchronizationByResource.Remove(key);
                continue;
            }
            _synchronizationByResource[key] = value;
            _synchronizationScratch = previous ?? new ResourceSynchronization();
            foreach (int pass in value.Passes.Keys)
            {
                _changedSynchronizationPasses.Add(pass);
                if (!_synchronizationByPass.TryGetValue(pass, out var resources))
                    _synchronizationByPass.Add(pass, resources = []);
                resources.Add(key);
            }
            foreach (int pass in value.WaitPasses)
            {
                if (!_initialWaitResources.TryGetValue(pass, out var resources))
                    _initialWaitResources.Add(pass, resources = []);
                resources.Add(key);
            }
            _lateReleases.AddRange(value.LateReleases);
            SaveSynchronizationInput(key);
        }
        foreach (int pass in _changedSynchronizationPasses)
        {
            InvalidateCommands(pass);
            _beforeBufferBarriers[pass].Clear(); _afterBufferBarriers[pass].Clear();
            _beforeTextureBarriers[pass].Clear(); _afterTextureBarriers[pass].Clear();
            _acquires[pass].Clear(); _releases[pass].Clear();
            _aliasBeforeResources[pass].Clear(); _aliasAfterResources[pass].Clear();
            _previousPhysicalPredecessors.Clear();
            _previousPhysicalPredecessors.AddRange(_physicalPredecessors[pass]);
            _physicalPredecessors[pass].Clear();
            foreach (var key in _synchronizationByPass[pass])
            {
                PassSynchronization value = _synchronizationByResource[key].Passes[pass];
                _beforeBufferBarriers[pass].AddRange(value.BeforeBuffers); _afterBufferBarriers[pass].AddRange(value.AfterBuffers);
                _beforeTextureBarriers[pass].AddRange(value.BeforeTextures); _afterTextureBarriers[pass].AddRange(value.AfterTextures);
                _acquires[pass].AddRange(value.Acquires); _releases[pass].AddRange(value.Releases);
                _aliasBeforeResources[pass].AddRange(value.AliasBefore); _aliasAfterResources[pass].AddRange(value.AliasAfter);
                foreach (int predecessor in value.Predecessors) AddUnique(_physicalPredecessors[pass], predecessor);
            }
            if (_previousPhysicalPredecessors.Count != _physicalPredecessors[pass].Count ||
                _previousPhysicalPredecessors.Any(predecessor => !_physicalPredecessors[pass].Contains(predecessor)))
                ChangeRecording(pass);
        }
        _synchronizationChanges.Clear();
        _changedSynchronizationPasses.Clear();
    }

    private void RefreshCompletionWaits(int waveCount)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.WaitCollection);
        for (int wave = 0; wave < waveCount; wave++)
            foreach (int submission in _recordingSubmissions[wave]) _submissionWaits[submission].Clear();
        foreach ((int pass, var resources) in _initialWaitResources)
        {
            if (!IsLive(pass)) continue;
            Queue queue = _passes[pass].Queue!;
            List<QueueCompletion> waits = _submissionWaits[_submissionByPass[pass]];
            foreach (var key in resources)
            {
                switch (key.Kind)
                {
                    case GraphAccessTargetKind.Buffer:
                        ref readonly FrameBuffer buffer = ref CollectionsMarshal.AsSpan(_buffers)[key.Index];
                        ReadOnlySpan<BufferBoundaryState> states = BufferEntryStates(buffer);
                        if (buffer.Lifetime == RenderGraphResourceLifetime.Persistent &&
                            buffer.Definition?.Boundaries.CompletionItems is { } items)
                        {
                            foreach (int item in items) AddWait(states[item].ReadyAfter);
                        }
                        else
                            foreach (ref readonly BufferBoundaryState state in states) AddWait(state.ReadyAfter);
                        break;
                    case GraphAccessTargetKind.Texture:
                        FrameTexture texture = _textures[key.Index];
                        foreach (TextureBoundaryState state in TextureEntryStates(texture)) AddWait(state.ReadyAfter);
                        break;
                    case GraphAccessTargetKind.QueryPool:
                        foreach (QueryBoundaryState state in _queryPools[key.Index].Definition.BoundaryStates) AddWait(state.ReadyAfter);
                        break;
                    case GraphAccessTargetKind.RayTracingShaderTable:
                        foreach (RayTracingShaderTableBoundaryState state in _shaderTables[key.Index].Definition.BoundaryStates) AddWait(state.ReadyAfter);
                        break;
                }
            }
            void AddWait(QueueCompletion? completion)
            {
                if (completion.HasValue && !ReferenceEquals(completion.Value.Queue, queue))
                    AddCompletion(waits, completion.Value);
            }
        }
    }


    private static ReadOnlySpan<BufferBoundaryState> BufferEntryStates(scoped in FrameBuffer buffer) =>
        buffer.Lifetime == RenderGraphResourceLifetime.Persistent
            ? buffer.Definition?.BoundaryStates ?? buffer.EntryBoundaryStates : buffer.EntryBoundaryStates;

    private static ReadOnlySpan<TextureBoundaryState> TextureEntryStates(scoped in FrameTexture texture) =>
        texture.Lifetime == RenderGraphResourceLifetime.Persistent
            ? texture.Definition?.BoundaryStates ?? texture.EntryBoundaryStates : texture.EntryBoundaryStates;

    private bool BufferSynchronizationStateChanged(int index, in FrameBuffer row)
    {
        BufferSyncInput input = _buffers[index].Synchronization;
        ReadOnlySpan<BufferBoundaryState> states = BufferEntryStates(row);
        if (row.Resource?.Device.Capabilities.SupportsConcurrentBufferReads == true)
        {
            if (!ReferenceEquals(input.Alias, row.Placement.AliasingPredecessor)) return true;
            int count = 0;
            foreach (BufferBoundaryState state in states)
            {
                if (state.ReleasedTo is null) continue;
                if (input.States is null || count >= input.States.Length ||
                    input.States[count++] != new BufferSyncState(state.Range, state.Sync, state.Access, state.Queue, state.ReleasedTo))
                    return true;
            }
            return count != (input.States?.Length ?? 0);
        }
        if (!ReferenceEquals(input.Alias, row.Placement.AliasingPredecessor) ||
            input.States is null || input.States.Length != states.Length) return true;
        for (int item = 0; item < states.Length; item++)
            if (input.States[item] != new BufferSyncState(states[item].Range, states[item].Sync, states[item].Access,
                states[item].Queue, states[item].ReleasedTo)) return true;
        return false;
    }

    private bool TextureSynchronizationStateChanged(int index, in FrameTexture row)
    {
        TextureSyncInput input = _textures[index].Synchronization;
        ReadOnlySpan<TextureBoundaryState> states = TextureEntryStates(row);
        if (!ReferenceEquals(input.Alias, row.Placement.AliasingPredecessor) ||
            input.States is null || input.States.Length != states.Length) return true;
        for (int item = 0; item < states.Length; item++)
            if (input.States[item] != new TextureSyncState(states[item].Range, states[item].Sync, states[item].Access,
                states[item].Layout, states[item].Queue, states[item].ReleasedTo, states[item].ReleaseRange)) return true;
        return false;
    }

    internal void BoundaryChanged(GraphIdentity identity, GraphStoreKind kind)
    {
        if (kind == GraphStoreKind.Buffers && _bufferIndices.TryGetValue(identity, out int buffer))
            _changedBoundaryResources.Add((GraphAccessTargetKind.Buffer, buffer));
        else if (kind == GraphStoreKind.Textures && _textureIndices.TryGetValue(identity, out int texture))
            _changedBoundaryResources.Add((GraphAccessTargetKind.Texture, texture));
        else if (kind == GraphStoreKind.QueryPools && _queryPoolIndices.TryGetValue(identity, out int query))
            _changedBoundaryResources.Add((GraphAccessTargetKind.QueryPool, query));
        else if (kind == GraphStoreKind.ShaderTables && _shaderTableIndices.TryGetValue(identity, out int table))
            _changedBoundaryResources.Add((GraphAccessTargetKind.RayTracingShaderTable, table));
    }

    private void ObserveChangedBoundaries()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.BoundaryObservation);
        foreach (var key in _changedBoundaryResources)
        {
            if (!ResourceExists(key)) continue;
            if (key.Kind == GraphAccessTargetKind.Buffer)
            {
                ref FrameBuffer row = ref CollectionsMarshal.AsSpan(_buffers)[key.Index];
                if (row.Ownership == RenderGraphResourceOwnership.CallerOwned &&
                    row.Lifetime == RenderGraphResourceLifetime.PerFrame &&
                    row.Definition is not null)
                    row.EntryBoundaryStates = _frame.TryGetBufferData(row.Identity, out var data)
                        ? data.BoundaryStates : row.Definition.BoundaryStates;
                ObserveBuffer(key.Index, ref row);
                ObserveBufferSynchronization(key.Index, _buffers[key.Index]);
            }
            else if (key.Kind == GraphAccessTargetKind.Texture)
            {
                ref FrameTexture row = ref CollectionsMarshal.AsSpan(_textures)[key.Index];
                if (row.Ownership == RenderGraphResourceOwnership.CallerOwned &&
                    row.Lifetime == RenderGraphResourceLifetime.PerFrame &&
                    row.Definition is not null)
                    row.EntryBoundaryStates = row.Definition.BoundaryStates;
                ObserveTexture(key.Index, ref row);
                ObserveTextureSynchronization(key.Index, _textures[key.Index]);
            }
            else if (key.Kind == GraphAccessTargetKind.QueryPool)
                ObserveQueryContents(key.Index);
            else if (key.Kind == GraphAccessTargetKind.RayTracingShaderTable)
                ObserveShaderTableContents(key.Index);
        }
        _changedBoundaryResources.Clear();
    }

    private void ObserveQueryContents(int index)
    {
        ref FrameQueryPool row = ref CollectionsMarshal.AsSpan(_queryPools)[index];
        row.EntryBoundaryStates = row.Definition.BoundaryStates;
        bool changed = row.Contents is null || row.Contents.Length != row.EntryBoundaryStates.Length;
        if (changed) row.Contents = new (QueryRange, ResourceContentState)[row.EntryBoundaryStates.Length];
        var contents = row.Contents!;
        for (int item = 0; item < row.EntryBoundaryStates.Length; item++)
        {
            QueryBoundaryState state = row.EntryBoundaryStates[item];
            var value = (state.Range, state.Contents);
            changed |= contents[item] != value;
            contents[item] = value;
        }
        if (changed) ChangeAnalysisResource(GraphAccessTargetKind.QueryPool, index);
    }

    private void ObserveShaderTableContents(int index)
    {
        ref FrameRayTracingShaderTable row = ref CollectionsMarshal.AsSpan(_shaderTables)[index];
        row.EntryBoundaryStates = row.Definition.BoundaryStates;
        ResourceContentState contents = ResolveShaderTableContents(row.EntryBoundaryStates);
        if (contents != row.Contents) ChangeAnalysisResource(GraphAccessTargetKind.RayTracingShaderTable, index);
        row.Contents = contents;
    }

    private void SaveSynchronizationInput((GraphAccessTargetKind Kind, int Index) key)
    {
        if (key.Kind == GraphAccessTargetKind.Buffer)
        {
            FrameBuffer row = _buffers[key.Index];
            ReadOnlySpan<BufferBoundaryState> states = BufferEntryStates(row);
            ref BufferSyncInput previous = ref CollectionsMarshal.AsSpan(_buffers)[key.Index].Synchronization;
            if (row.Resource?.Device.Capabilities.SupportsConcurrentBufferReads == true)
            {
                int count = 0;
                foreach (BufferBoundaryState state in states) if (state.ReleasedTo is not null) count++;
                BufferSyncState[] released = previous.States is { } saved && saved.Length == count ? saved : new BufferSyncState[count];
                int item = 0;
                foreach (BufferBoundaryState state in states)
                    if (state.ReleasedTo is not null)
                        released[item++] = new(state.Range, state.Sync, state.Access, state.Queue, state.ReleasedTo);
                previous = new(row.Resource, row.Placement.AliasingPredecessor, released);
                return;
            }
            BufferSyncState[] values = previous.States is { } old && old.Length == states.Length ? old : new BufferSyncState[states.Length];
            for (int item = 0; item < states.Length; item++)
                values[item] = new(states[item].Range, states[item].Sync, states[item].Access, states[item].Queue, states[item].ReleasedTo);
            previous = new(row.Resource, row.Placement.AliasingPredecessor, values);
        }
        else if (key.Kind == GraphAccessTargetKind.Texture)
        {
            FrameTexture row = _textures[key.Index];
            ReadOnlySpan<TextureBoundaryState> states = TextureEntryStates(row);
            ref TextureSyncInput previous = ref CollectionsMarshal.AsSpan(_textures)[key.Index].Synchronization;
            TextureSyncState[] values = previous.States is { } old && old.Length == states.Length ? old : new TextureSyncState[states.Length];
            for (int item = 0; item < states.Length; item++)
                values[item] = new(states[item].Range, states[item].Sync, states[item].Access, states[item].Layout,
                    states[item].Queue, states[item].ReleasedTo, states[item].ReleaseRange);
            previous = new(row.Resource, row.Placement.AliasingPredecessor, values);
        }
    }

    private readonly Dictionary<TextureBarrier, int> _textureBarrierGroups = [];
    private readonly List<TextureQueueTransfer> _textureQueueTransfers = [];
    private readonly Dictionary<TextureQueueTransfer, int> _textureTransferGroups = [];

    private void CompactTextureBarriers()
    {
        foreach (PassSynchronization pass in _collectSynchronization.Passes.Values)
        {
            CompactRanges(pass.BeforeTextures, _textureBarrierGroups,
                static value => value.Phase == BarrierPhase.Complete ? value.Range : (TextureSubresourceRange?)null,
                static (value, range) => value with { Range = range });
            CompactRanges(pass.AfterTextures, _textureBarrierGroups,
                static value => value.Phase == BarrierPhase.Complete ? value.Range : (TextureSubresourceRange?)null,
                static (value, range) => value with { Range = range });
        }
        CompactRanges(_textureQueueTransfers, _textureTransferGroups,
            static value => value.Range, static (value, range) => value with { Range = range });
        foreach (TextureQueueTransfer transfer in _textureQueueTransfers)
        {
            Synchronization(transfer.ConsumerPass).Acquires.Add(transfer.Acquire with { TextureRange = transfer.Range });
            if (transfer.ProducerPass >= 0)
                Synchronization(transfer.ProducerPass).Releases.Add(transfer.Release with { TextureRange = transfer.Range });
            else
                _collectSynchronization.LateReleases.Add(new PendingQueueRelease(transfer.SourceQueue,
                    transfer.DestinationQueue, transfer.ConsumerPass, transfer.Release.Resource,
                    transfer.Range, transfer.Release.Sync, transfer.Release.Access, transfer.Release.Layout));
        }
        _textureQueueTransfers.Clear();
    }

    private void AddTextureQueueTransfer(int producer, int consumer, Texture texture,
        TextureSubresourceRange range, Queue source, Queue destination,
        PipelineSync beforeSync, PipelineSync afterSync, ResourceAccess beforeAccess,
        ResourceAccess afterAccess, TextureLayout beforeLayout, TextureLayout afterLayout)
    {
        _textureQueueTransfers.Add(new TextureQueueTransfer(producer, consumer, source, destination, range,
            new QueueRelease(texture, null, beforeSync, beforeAccess, beforeLayout, destination.Type),
            new QueueAcquire(texture, null, source.Type, afterSync, afterAccess, afterLayout)));
    }

    // Release and acquire ranges must be identical, including when neighboring
    // cells have different producer states or are consumed by different passes.
    private readonly record struct TextureQueueTransfer(int ProducerPass, int ConsumerPass,
        Queue SourceQueue, Queue DestinationQueue, TextureSubresourceRange Range,
        QueueRelease Release, QueueAcquire Acquire);

    // Coalesce rectangles along one axis at a time. All resource, synchronization,
    // layout, queue and split-phase fields remain part of the equality key.
    private static void CompactRanges<T>(List<T> barriers, Dictionary<T, int> groups,
        Func<T, TextureSubresourceRange?> getRange,
        Func<T, TextureSubresourceRange, T> setRange) where T : notnull
    {
        for (int axis = 0; axis < 2 && barriers.Count > 1; axis++)
        {
            groups.Clear();
            int count = 0;
            for (int index = 0; index < barriers.Count; index++)
            {
                T value = barriers[index];
                if (getRange(value) is not { } range)
                {
                    barriers[count++] = value;
                    continue;
                }
                T key = setRange(value, axis == 0
                    ? range with { FirstMipLevel = 0, MipLevelCount = 0 }
                    : range with { FirstArrayLayer = 0, ArrayLayerCount = 0 });
                if (groups.TryGetValue(key, out int previousIndex))
                {
                    TextureSubresourceRange previous = getRange(barriers[previousIndex])!.Value;
                    uint first = axis == 0 ? range.FirstMipLevel : range.FirstArrayLayer;
                    uint length = axis == 0 ? range.MipLevelCount : range.ArrayLayerCount;
                    uint previousFirst = axis == 0 ? previous.FirstMipLevel : previous.FirstArrayLayer;
                    uint previousLength = axis == 0 ? previous.MipLevelCount : previous.ArrayLayerCount;
                    if ((ulong)previousFirst + previousLength == first ||
                        (ulong)first + length == previousFirst)
                    {
                        TextureSubresourceRange combined = axis == 0
                            ? previous with { FirstMipLevel = Math.Min(first, previousFirst),
                                MipLevelCount = checked(length + previousLength) }
                            : previous with { FirstArrayLayer = Math.Min(first, previousFirst),
                                ArrayLayerCount = checked(length + previousLength) };
                        barriers[previousIndex] = setRange(value, combined);
                        continue;
                    }
                }
                groups[key] = count;
                barriers[count++] = value;
            }
            if (count < barriers.Count)
                barriers.RemoveRange(count, barriers.Count - count);
        }
        groups.Clear();
    }
}

