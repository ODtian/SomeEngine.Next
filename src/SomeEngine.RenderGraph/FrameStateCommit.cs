namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly List<BufferStateValue> _bufferBoundaryScratch = [];
    private readonly List<TextureStateValue> _textureBoundaryScratch = [];
    private readonly List<BufferStateValue> _bufferContentScratch = [];
    private readonly List<TextureStateValue> _textureContentScratch = [];
    private readonly List<TextureStateValue> _textureWriteScratch = [];
    private List<BufferStateUpdate>[] _submissionBufferUpdates = [];
    private List<TextureStateUpdate>[] _submissionTextureUpdates = [];
    private List<QueryStateUpdate>[] _submissionQueryUpdates = [];
    private List<ShaderTableStateUpdate>[] _submissionShaderTableUpdates = [];
    private readonly List<QueryStateValue> _queryBoundaryScratch = [];
    private readonly List<QueryStateValue> _queryContentScratch = [];
    private readonly List<ShaderTableStateValue> _shaderTableBoundaryScratch = [];
    private readonly List<int> _priorCompletionItems = [];
    private readonly HashSet<int> _bufferCompletionSources = [];
    private readonly List<int> _bufferCompletionItems = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _lateStateResources = [];
    private readonly List<BufferStateUpdate> _lateBufferStateUpdates = [];
    private readonly List<TextureStateUpdate> _lateTextureStateUpdates = [];
    private int _lateBufferStateCount;
    private int _lateTextureStateCount;
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _stateConnectionChanges = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _stateUpdateResourcesChanged = [];
    private List<BufferStateUpdate>?[] _bufferUpdatesByResource = [];
    private List<TextureStateUpdate>?[] _textureUpdatesByResource = [];
    private List<QueryStateUpdate>?[] _queryUpdatesByResource = [];
    private List<ShaderTableStateUpdate>?[] _shaderTableUpdatesByResource = [];

    private readonly record struct TextureStateValue(TextureBoundaryState State, int CompletionSource,
        int LastPass = -1, bool MergeableWrites = false);
    private sealed class TextureStateUpdate
    {
        internal int Submission;
        internal int ResourceIndex;
        internal int[] Accesses = [];
        internal bool[] Acquired = [];
        internal QueueRelease[] Releases = [];
        internal bool Initialized;
        internal bool BoundaryChanged = true;
        internal TextureBoundaryState[] Before = [];
        internal TextureBoundaryState[]? InputStates;
        internal int[] PriorCompletionItems = [];
        internal int[] CompletionSources = [];
        internal TextureBoundaryState[] Values = [];
        internal QueueCompletion?[] Completions = [];
    }

    private readonly record struct BufferStateValue(BufferBoundaryState State, int CompletionSource);
    private readonly record struct BufferStateStep(int Pass, int[] Accesses,
        PipelineSync Sync, ResourceAccess Access, bool Writes, bool Acquired, bool MergeableWrites);

    private sealed class BufferStateUpdate
    {
        internal int Submission;
        internal int ResourceIndex;
        internal BufferStateStep[] Steps = [];
        internal QueueRelease[] Releases = [];
        internal bool Initialized;
        internal bool BoundaryChanged = true;
        internal BufferBoundaryState[] Before = [];
        internal BufferBoundaryState[]? InputStates;
        internal int[] PriorCompletionItems = [];
        internal int[] CompletionSources = [];
        internal BufferBoundaries Boundaries = new([]);
        internal BufferBoundaryState[] Values => Boundaries.States;
        internal QueueCompletion?[] Completions = [];
    }

    private readonly record struct QueryStateValue(QueryBoundaryState State, int CompletionSource);
    private sealed class QueryStateUpdate
    {
        internal int Submission;
        internal int ResourceIndex;
        internal int[] Accesses = [];
        internal bool Initialized;
        internal QueryBoundaryState[] Before = [];
        internal QueryBoundaryState[]? InputStates;
        internal int[] PriorCompletionItems = [];
        internal int[] CompletionSources = [];
        internal QueryBoundaryState[] Values = [];
        internal QueueCompletion?[] Completions = [];
    }

    private readonly record struct ShaderTableStateValue(RayTracingShaderTableBoundaryState State, int CompletionSource);
    private sealed class ShaderTableStateUpdate
    {
        internal int Submission;
        internal int ResourceIndex;
        internal int[] Accesses = [];
        internal bool Initialized;
        internal RayTracingShaderTableBoundaryState[] Before = [];
        internal RayTracingShaderTableBoundaryState[]? InputStates;
        internal int[] PriorCompletionItems = [];
        internal int[] CompletionSources = [];
        internal RayTracingShaderTableBoundaryState[] Values = [];
        internal QueueCompletion?[] Completions = [];
    }

    // Relationships are built with the submission graph. The normal frame publishes one
    // resource result per successful submission, without visiting the intermediate passes.
    private void PrepareStateUpdates()
    {
        ResizeStateResourceLists();
        GrowStateLists(ref _submissionBufferUpdates, _submissionCount);
        GrowStateLists(ref _submissionTextureUpdates, _submissionCount);
        GrowStateLists(ref _submissionQueryUpdates, _submissionCount);
        GrowStateLists(ref _submissionShaderTableUpdates, _submissionCount);
        RefreshStateUpdates();
    }

    private static void GrowStateLists<T>(ref List<T>[] values, int count)
    {
        int previous = values.Length;
        EnsureCapacity(ref values, count);
        for (int index = previous; index < values.Length; index++) values[index] = [];
    }

    private void ResizeStateResourceLists()
    {
        if (_bufferUpdatesByResource.Length < _buffers.Count) Array.Resize(ref _bufferUpdatesByResource, _buffers.Count);
        if (_textureUpdatesByResource.Length < _textures.Count) Array.Resize(ref _textureUpdatesByResource, _textures.Count);
        if (_queryUpdatesByResource.Length < _queryPools.Count) Array.Resize(ref _queryUpdatesByResource, _queryPools.Count);
        if (_shaderTableUpdatesByResource.Length < _shaderTables.Count) Array.Resize(ref _shaderTableUpdatesByResource, _shaderTables.Count);
    }

    private void BuildResourceStateUpdates((GraphAccessTargetKind Kind, int Index) key)
    {
        if (!_resourceAccessLists.TryGetValue(key, out List<int>? source) || source.Count == 0) return;
        var groups = new Dictionary<int, List<int>>();
        foreach (int index in source)
        {
            FrameResourceAccess access = _accesses[index];
            if (!IsLive(access.PassIndex)) continue;
            int submission = _submissionByPass[access.PassIndex];
            if (submission < 0) continue;
            if (!groups.TryGetValue(submission, out List<int>? indices)) groups.Add(submission, indices = []);
            indices.Add(index);
        }
        foreach ((int submission, List<int> indices) in groups)
        {
            indices.Sort((left, right) =>
            {
                FrameResourceAccess a = _accesses[left], b = _accesses[right];
                int order = _executionOrder.Position(a.PassIndex).CompareTo(_executionOrder.Position(b.PassIndex));
                return order != 0 ? order : a.PassAdjacencyIndex.CompareTo(b.PassAdjacencyIndex);
            });
            if (key.Kind == GraphAccessTargetKind.Buffer)
            {
                var steps = new List<BufferStateStep>();
                for (int first = 0; first < indices.Count;)
                {
                    int pass = _accesses[indices[first]].PassIndex;
                    int end = first;
                    PipelineSync sync = default;
                    ResourceAccess access = default;
                    bool writes = false;
                    bool mergeableWrites = true;
                    while (end < indices.Count && _accesses[indices[end]].PassIndex == pass)
                    {
                        FrameResourceAccess value = _accesses[indices[end++]];
                        sync |= value.Sync;
                        access = MergeBufferAccess(access, value.Access);
                        writes |= value.Mode != GraphAccessMode.Read;
                        mergeableWrites &= value.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered;
                    }
                    steps.Add(new(pass, CollectionsMarshal.AsSpan(indices).Slice(first, end - first).ToArray(), sync, access, writes,
                        HasIncomingBufferAcquire(pass, _buffers[key.Index].Resource), mergeableWrites));
                    first = end;
                }
                var update = new BufferStateUpdate { Submission = submission, ResourceIndex = key.Index,
                    Steps = steps.ToArray(), Releases = ResourceReleases(submission, _buffers[key.Index].Resource) };
                _submissionBufferUpdates[submission].Add(update);
                (_bufferUpdatesByResource[key.Index] ??= []).Add(update);
            }
            else if (key.Kind == GraphAccessTargetKind.Texture)
            {
                var acquired = new bool[indices.Count];
                for (int item = 0; item < indices.Count; item++)
                {
                    FrameResourceAccess access = _accesses[indices[item]];
                    acquired[item] = HasIncomingTextureAcquire(access.PassIndex, _textures[key.Index].Resource, access.TextureRange);
                }
                var update = new TextureStateUpdate { Submission = submission, ResourceIndex = key.Index,
                    Accesses = indices.ToArray(), Acquired = acquired,
                    Releases = ResourceReleases(submission, _textures[key.Index].Resource) };
                _submissionTextureUpdates[submission].Add(update);
                (_textureUpdatesByResource[key.Index] ??= []).Add(update);
            }
            else if (key.Kind == GraphAccessTargetKind.QueryPool)
            {
                var update = new QueryStateUpdate { Submission = submission, ResourceIndex = key.Index, Accesses = indices.ToArray() };
                _submissionQueryUpdates[submission].Add(update);
                (_queryUpdatesByResource[key.Index] ??= []).Add(update);
            }
            else
            {
                var update = new ShaderTableStateUpdate { Submission = submission, ResourceIndex = key.Index, Accesses = indices.ToArray() };
                _submissionShaderTableUpdates[submission].Add(update);
                (_shaderTableUpdatesByResource[key.Index] ??= []).Add(update);
            }
        }
    }

    private QueueRelease[] ResourceReleases(int submission, Resource? resource)
    {
        var result = new List<QueueRelease>();
        foreach (int pass in _submissionPasses[submission])
            foreach (QueueRelease release in _releases[pass])
                if (ReferenceEquals(release.Resource, resource)) result.Add(release);
        return result.ToArray();
    }

    private Queue ReleaseDestination(QueueType type)
    {
        foreach (Queue queue in _frame.Graph.Queues)
            if (queue.Type == type) return queue;
        DevelopmentCheck.Fail("A queue release destination must be registered with the graph.");
        return null!;
    }

    private void RefreshStateConnections()
    {
        if (_stateConnectionChanges.Count == 0) return;
        ResizeStateResourceLists();
        foreach (var key in _stateConnectionChanges)
        {
            switch (key.Kind)
            {
                case GraphAccessTargetKind.Buffer:
                    if ((uint)key.Index < (uint)_bufferUpdatesByResource.Length && _bufferUpdatesByResource[key.Index] is { } buffers)
                    {
                        foreach (BufferStateUpdate update in buffers) _submissionBufferUpdates[update.Submission].Remove(update);
                        buffers.Clear();
                    }
                    break;
                case GraphAccessTargetKind.Texture:
                    if ((uint)key.Index < (uint)_textureUpdatesByResource.Length && _textureUpdatesByResource[key.Index] is { } textures)
                    {
                        foreach (TextureStateUpdate update in textures) _submissionTextureUpdates[update.Submission].Remove(update);
                        textures.Clear();
                    }
                    break;
                case GraphAccessTargetKind.QueryPool:
                    if ((uint)key.Index < (uint)_queryUpdatesByResource.Length && _queryUpdatesByResource[key.Index] is { } queries)
                    {
                        foreach (QueryStateUpdate update in queries) _submissionQueryUpdates[update.Submission].Remove(update);
                        queries.Clear();
                    }
                    break;
                case GraphAccessTargetKind.RayTracingShaderTable:
                    if ((uint)key.Index < (uint)_shaderTableUpdatesByResource.Length && _shaderTableUpdatesByResource[key.Index] is { } tables)
                    {
                        foreach (ShaderTableStateUpdate update in tables) _submissionShaderTableUpdates[update.Submission].Remove(update);
                        tables.Clear();
                    }
                    break;
            }
            BuildResourceStateUpdates(key);
        }
        _stateConnectionChanges.Clear();
    }

    private void RefreshStateUpdates()
    {
        RefreshStateConnections();
        foreach ((GraphAccessTargetKind kind, int index) in _stateUpdateResourcesChanged)
        {
            if (kind == GraphAccessTargetKind.Buffer && (uint)index < (uint)_bufferUpdatesByResource.Length)
            {
                foreach (BufferStateUpdate update in _bufferUpdatesByResource[index] ?? [])
                {
                    update.Initialized = false;
                    update.Releases = ResourceReleases(update.Submission, _buffers[index].Resource);
                    for (int item = 0; item < update.Steps.Length; item++)
                    {
                        BufferStateStep step = update.Steps[item];
                        PipelineSync sync = default;
                        ResourceAccess access = default;
                        bool writes = false;
                        foreach (int accessIndex in step.Accesses)
                        {
                            FrameResourceAccess value = _accesses[accessIndex];
                            sync |= value.Sync;
                            access = MergeBufferAccess(access, value.Access);
                            writes |= value.Mode != GraphAccessMode.Read;
                        }
                        update.Steps[item] = step with { Sync = sync, Access = access, Writes = writes,
                            Acquired = HasIncomingBufferAcquire(step.Pass, _buffers[index].Resource) };
                    }
                }
            }
            else if (kind == GraphAccessTargetKind.Texture && (uint)index < (uint)_textureUpdatesByResource.Length)
            {
                foreach (TextureStateUpdate update in _textureUpdatesByResource[index] ?? [])
                {
                    update.Initialized = false;
                    update.Releases = ResourceReleases(update.Submission, _textures[index].Resource);
                    for (int item = 0; item < update.Accesses.Length; item++)
                    {
                        FrameResourceAccess access = _accesses[update.Accesses[item]];
                        update.Acquired[item] = HasIncomingTextureAcquire(access.PassIndex, _textures[index].Resource, access.TextureRange);
                    }
                }
            }
            else if (kind == GraphAccessTargetKind.QueryPool && (uint)index < (uint)_queryUpdatesByResource.Length)
                foreach (QueryStateUpdate update in _queryUpdatesByResource[index] ?? []) update.Initialized = false;
            else if (kind == GraphAccessTargetKind.RayTracingShaderTable && (uint)index < (uint)_shaderTableUpdatesByResource.Length)
                foreach (ShaderTableStateUpdate update in _shaderTableUpdatesByResource[index] ?? []) update.Initialized = false;
        }
        _stateUpdateResourcesChanged.Clear();
    }

    // State result arrays retain their ranges and access fields until a new array is
    // published; only completions change in place. Caller-owned per-frame bindings
    // can overwrite their arrays, so those inputs still compare every state.
    // Construct changed range results before Submit can succeed. No externally visible
    // boundary is changed until the backend returns a completion for this submission.
    private void PrepareSubmissionState(int submission)
    {
        foreach (BufferStateUpdate update in _submissionBufferUpdates[submission])
        {
            ref readonly FrameBuffer buffer = ref CollectionsMarshal.AsSpan(_buffers)[update.ResourceIndex];
            BufferBoundaryState[] current = buffer.Lifetime == RenderGraphResourceLifetime.Persistent
                ? buffer.Definition?.BoundaryStates ?? buffer.EntryBoundaryStates ?? []
                : buffer.EntryBoundaryStates ?? [];
            bool sameInput = (buffer.Lifetime == RenderGraphResourceLifetime.Persistent ||
                buffer.Ownership == RenderGraphResourceOwnership.GraphOwned) &&
                ReferenceEquals(update.InputStates, current);
            bool changed = !update.Initialized || (!sameInput && update.Before.Length != current.Length);
            if (!changed && !sameInput)
                for (int item = 0; item < current.Length; item++)
                    if (!BufferBoundaryMatches(update.Before[item], current[item])) { changed = true; break; }
            if (changed) BuildBufferStateUpdate(update, current);
            update.InputStates = current;
            foreach (int item in update.PriorCompletionItems)
                update.Completions[item] = current[update.CompletionSources[item]].ReadyAfter;
        }
        foreach (TextureStateUpdate update in _submissionTextureUpdates[submission])
        {
            ref readonly FrameTexture texture = ref CollectionsMarshal.AsSpan(_textures)[update.ResourceIndex];
            TextureBoundaryState[] current = texture.Lifetime == RenderGraphResourceLifetime.Persistent
                ? texture.Definition?.BoundaryStates ?? texture.EntryBoundaryStates ?? []
                : texture.EntryBoundaryStates ?? [];
            bool sameInput = (texture.Lifetime == RenderGraphResourceLifetime.Persistent ||
                texture.Ownership == RenderGraphResourceOwnership.GraphOwned) &&
                ReferenceEquals(update.InputStates, current);
            bool changed = !update.Initialized || (!sameInput && update.Before.Length != current.Length);
            if (!changed && !sameInput)
                for (int item = 0; item < current.Length; item++)
                    if (!TextureBoundaryMatches(update.Before[item], current[item])) { changed = true; break; }
            if (changed) BuildTextureStateUpdate(update, current);
            update.InputStates = current;
            foreach (int item in update.PriorCompletionItems)
                update.Completions[item] = current[update.CompletionSources[item]].ReadyAfter;
        }
        foreach (QueryStateUpdate update in _submissionQueryUpdates[submission])
        {
            QueryBoundaryState[] current = _queryPools[update.ResourceIndex].Definition.BoundaryStates;
            bool sameInput = ReferenceEquals(update.InputStates, current);
            bool changed = !update.Initialized || (!sameInput && update.Before.Length != current.Length);
            if (!changed && !sameInput)
                for (int item = 0; item < current.Length; item++)
                    if (update.Before[item] != (current[item] with { ReadyAfter = null })) { changed = true; break; }
            if (changed) BuildQueryStateUpdate(update, current);
            update.InputStates = current;
            foreach (int item in update.PriorCompletionItems)
                update.Completions[item] = current[update.CompletionSources[item]].ReadyAfter;
        }
        foreach (ShaderTableStateUpdate update in _submissionShaderTableUpdates[submission])
        {
            RayTracingShaderTableBoundaryState[] current = _shaderTables[update.ResourceIndex].Definition.BoundaryStates;
            bool sameInput = ReferenceEquals(update.InputStates, current);
            bool changed = !update.Initialized || (!sameInput && update.Before.Length != current.Length);
            if (!changed && !sameInput)
                for (int item = 0; item < current.Length; item++)
                    if (update.Before[item] != (current[item] with { ReadyAfter = null })) { changed = true; break; }
            if (changed) BuildShaderTableStateUpdate(update, current);
            update.InputStates = current;
            foreach (int item in update.PriorCompletionItems)
                update.Completions[item] = current[update.CompletionSources[item]].ReadyAfter;
        }
    }

    private static bool BufferBoundaryMatches(in BufferBoundaryState left, in BufferBoundaryState right) =>
        left.Range == right.Range && left.Sync == right.Sync && left.Access == right.Access &&
        left.Contents == right.Contents && left.ContentScope == right.ContentScope &&
        ReferenceEquals(left.Queue, right.Queue) && ReferenceEquals(left.ReleasedTo, right.ReleasedTo);

    private static bool TextureBoundaryMatches(in TextureBoundaryState left, in TextureBoundaryState right) =>
        left.Range == right.Range && left.Sync == right.Sync && left.Access == right.Access &&
        left.Layout == right.Layout && left.Contents == right.Contents && left.ReleaseRange == right.ReleaseRange &&
        ReferenceEquals(left.Queue, right.Queue) && ReferenceEquals(left.ReleasedTo, right.ReleasedTo);

    private void CommitSubmissionState(int submission, QueueCompletion completion)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.StateCommit);
        foreach (BufferStateUpdate update in _submissionBufferUpdates[submission])
        {
            CommitBuffer(update, completion);
        }
        foreach (TextureStateUpdate update in _submissionTextureUpdates[submission])
        {
            CommitTexture(update, completion);
        }
        foreach (QueryStateUpdate update in _submissionQueryUpdates[submission])
        {
            for (int item = 0; item < update.Values.Length; item++)
                update.Values[item].SetCompletion(
                    update.CompletionSources[item] < 0 ? completion : update.Completions[item]);
            ref FrameQueryPool pool = ref CollectionsMarshal.AsSpan(_queryPools)[update.ResourceIndex];
            pool.Definition.BoundaryStates = update.Values;
            pool.EntryBoundaryStates = update.Values;
            _frame.Graph.BoundaryChanged(pool.Identity, GraphStoreKind.QueryPools);
        }
        foreach (ShaderTableStateUpdate update in _submissionShaderTableUpdates[submission])
        {
            for (int item = 0; item < update.Values.Length; item++)
                update.Values[item].SetCompletion(
                    update.CompletionSources[item] < 0 ? completion : update.Completions[item]);
            ref FrameRayTracingShaderTable table = ref CollectionsMarshal.AsSpan(_shaderTables)[update.ResourceIndex];
            table.Definition.BoundaryStates = update.Values;
            table.EntryBoundaryStates = update.Values;
            _frame.Graph.BoundaryChanged(table.Identity, GraphStoreKind.ShaderTables);
        }
    }

    private void CommitBuffer(BufferStateUpdate update, QueueCompletion completion)
    {
        BufferBoundaryState[] boundaryStates = update.Values;
        for (int item = 0; item < boundaryStates.Length; item++)
            boundaryStates[item].SetCompletion(
                update.CompletionSources[item] < 0 ? completion : update.Completions[item]);
        StoreBufferStates(update.ResourceIndex, update.Boundaries, update.BoundaryChanged);
    }

    private void StoreBufferStates(int resourceIndex, BufferBoundaries boundaries, bool boundaryChanged)
    {
        BufferBoundaryState[] boundaryStates = boundaries.States;
        ref FrameBuffer frameBuffer = ref CollectionsMarshal.AsSpan(_buffers)[resourceIndex];
        // Upload storage is reused independently for each frame in flight. Its
        // last submitted state belongs to that buffer, not the graph declaration.
        if (frameBuffer.UploadData is { } upload)
        {
            upload.BoundaryStates = boundaryStates;
            frameBuffer.EntryBoundaryStates = boundaryStates;
            if (boundaryChanged) _changedBoundaryResources.Add((GraphAccessTargetKind.Buffer, resourceIndex));
            return;
        }
        if (frameBuffer.Lifetime == RenderGraphResourceLifetime.Persistent && frameBuffer.Definition is { } resource)
            resource.Boundaries = boundaries;
        else
        {
            frameBuffer.EntryBoundaryStates = boundaryStates;
            if (frameBuffer.Ownership == RenderGraphResourceOwnership.CallerOwned && frameBuffer.Definition is not null)
                frameBuffer.Definition.Boundaries = boundaries;
            if (frameBuffer.Lifetime == RenderGraphResourceLifetime.PerFrame && frameBuffer.Resource is not null)
                _frame.Slot.TransientResources?.StoreCachedBufferStates(frameBuffer.AllocationIdentity, frameBuffer.Resource, boundaryStates);
        }
        // Completion values are collected independently on every frame. Caller-owned
        // per-frame inputs still notify other frames so their boundary arrays refresh.
        if (!boundaryChanged && (frameBuffer.Lifetime == RenderGraphResourceLifetime.Persistent ||
            frameBuffer.Ownership == RenderGraphResourceOwnership.GraphOwned)) return;
        if (frameBuffer.Definition is not null && (frameBuffer.Lifetime == RenderGraphResourceLifetime.Persistent ||
            frameBuffer.Ownership == RenderGraphResourceOwnership.CallerOwned))
            _frame.Graph.BoundaryChanged(frameBuffer.Identity, GraphStoreKind.Buffers);
        else _changedBoundaryResources.Add((GraphAccessTargetKind.Buffer, resourceIndex));
    }

    private void BuildBufferStateUpdate(BufferStateUpdate update, ReadOnlySpan<BufferBoundaryState> current)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.StateUpdateConstruction);
        PrepareArray(ref update.Before, current.Length);
        _bufferBoundaryScratch.Clear();
        for (int item = 0; item < current.Length; item++)
        {
            BufferBoundaryState state = current[item] with { ReadyAfter = null };
            update.Before[item] = state;
            state = state with { Range = GraphStructureIndex.ResolveRange(state.Range,
                _buffers[update.ResourceIndex].Description.Size) };
            _bufferBoundaryScratch.Add(new(state, item));
        }
        PipelineSync previousSync = default;
        ResourceAccess previousAccess = default;
        Queue? previousQueue = null;
        bool previousMergeableWrites = false;
        BufferStateStep? previousRead = null;
        foreach (BufferStateStep step in update.Steps)
        {
            // All steps in this result share one submission completion. Consecutive
            // reads with identical ranges and state leave the same boundary result;
            // do not split and rewrite every resident geometry range for each reader.
            if (!step.Writes && !step.Acquired && previousRead is { } read &&
                read.Sync == step.Sync && read.Access == step.Access && SameBufferReads(read.Accesses, step.Accesses))
                continue;
            previousRead = !step.Writes && !step.Acquired ? step : null;
            Queue? queue = _passes[step.Pass].Queue;
            PipelineSync sync = step.Sync;
            if (previousMergeableWrites && step.MergeableWrites && !step.Acquired &&
                ReferenceEquals(previousQueue, queue) && previousAccess == step.Access)
                sync |= previousSync;
            previousSync = sync;
            previousAccess = step.Access;
            previousQueue = queue;
            previousMergeableWrites = step.MergeableWrites;
            bool retainDistinctQueueReaders = !step.Writes && !step.Acquired;
            BufferBoundaryState endpoint = default;
            foreach (int index in step.Accesses)
            {
                FrameResourceAccess access = _accesses[index];
                foreach (BufferRange range in access.BufferRanges)
                {
                    ReadOnlySpan<BufferStateValue> states = CollectionsMarshal.AsSpan(_bufferBoundaryScratch);
                    ResourceContentState contents = access.ResultContents ?? access.Mode switch
                    {
                        GraphAccessMode.Read or GraphAccessMode.ReadWrite => ResourceContentState.Defined,
                        GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete => ResourceContentState.Defined,
                        _ => ResourceContentState.Undefined,
                    };
                    BufferContentScope contentScope = contents == ResourceContentState.Defined
                        ? access.ResultContents.HasValue
                            ? access.BufferContentScope
                            : access.Mode switch
                            {
                                GraphAccessMode.Read => FindBufferContentScope(states, range),
                                GraphAccessMode.ReadWrite => access.BufferContentScope,
                                GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete => BufferContentScope.FullRange,
                                _ => FindBufferContentScope(states, range),
                            }
                        : BufferContentScope.FullRange;
                    endpoint = new BufferBoundaryState(range, sync, step.Access,
                        contents, _passes[step.Pass].Queue, null, contentScope);
                    bool preserveContents = access.Mode == GraphAccessMode.Write &&
                        access.Coverage == WriteCoverage.Partial && !access.ResultContents.HasValue;
                    if (preserveContents)
                    {
                        _bufferContentScratch.Clear();
                        _bufferContentScratch.AddRange(_bufferBoundaryScratch);
                    }
                    UpdateBufferBoundaryStates(_bufferBoundaryScratch, new(endpoint, -1), retainDistinctQueueReaders);
                    if (preserveContents)
                    {
                        // A partial write changes synchronization for its declared range,
                        // but preserves the contents of each previously defined subrange.
                        foreach (BufferStateValue previous in _bufferContentScratch)
                        {
                            if (previous.State.Contents != ResourceContentState.Defined) continue;
                            ulong start = Math.Max(previous.State.Range.Offset, range.Offset);
                            ulong end = Math.Min(previous.State.Range.Offset + previous.State.Range.Size,
                                range.Offset + range.Size);
                            if (start >= end) continue;
                            UpdateBufferBoundaryStates(_bufferBoundaryScratch, new(endpoint with
                            {
                                Range = new(start, end - start), Contents = previous.State.Contents,
                                ContentScope = previous.State.ContentScope,
                            }, -1), reader: false);
                        }
                        _bufferContentScratch.Clear();
                    }
                }
            }
            if (endpoint.Queue?.Device.Capabilities.SupportsConcurrentBufferReads != true)
                PublishBufferPhysicalState(CollectionsMarshal.AsSpan(_bufferBoundaryScratch), endpoint,
                    retainDistinctQueueReaders, step.Acquired);
        }
        foreach (QueueRelease release in update.Releases)
            ReleaseBufferStates(_bufferBoundaryScratch, _passes[_submissionPasses[update.Submission][0]].Queue!,
                ReleaseDestination(release.DestinationQueueType));
        _bufferBoundaryScratch.Sort(static (left, right) => left.State.Range.Offset.CompareTo(right.State.Range.Offset));
        // Published boundaries can remain in a resource cache after this update is reused.
        BufferBoundaryState[] values = new BufferBoundaryState[_bufferBoundaryScratch.Count];
        PrepareArray(ref update.CompletionSources, values.Length);
        PrepareArray(ref update.Completions, values.Length);
        _priorCompletionItems.Clear();
        for (int item = 0; item < values.Length; item++)
        {
            BufferStateValue value = _bufferBoundaryScratch[item];
            values[item] = value.State;
            update.CompletionSources[item] = value.CompletionSource;
            if (value.CompletionSource >= 0) _priorCompletionItems.Add(item);
        }
        update.Boundaries = new(values, BuildBufferCompletionItems(update.CompletionSources));
        update.BoundaryChanged = current.Length != update.Values.Length;
        for (int item = 0; !update.BoundaryChanged && item < current.Length; item++)
            update.BoundaryChanged = !BufferBoundaryMatches(current[item], update.Values[item]);
        update.PriorCompletionItems = _priorCompletionItems.ToArray();
        update.Initialized = true;
    }

    private int[] BuildBufferCompletionItems(ReadOnlySpan<int> sources)
    {
        _bufferCompletionSources.Clear();
        _bufferCompletionItems.Clear();
        for (int item = 0; item < sources.Length; item++)
            if (_bufferCompletionSources.Add(sources[item])) _bufferCompletionItems.Add(item);
        return _bufferCompletionItems.ToArray();
    }

    private bool SameBufferReads(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            FrameResourceAccess a = _accesses[left[index]], b = _accesses[right[index]];
            if (a.BufferRange != b.BufferRange || !ReferenceEquals(a.SharedBufferRanges, b.SharedBufferRanges) ||
                a.BufferContentScope != b.BufferContentScope ||
                a.ResultContents != b.ResultContents) return false;
        }
        return true;
    }

    private void CommitTexture(TextureStateUpdate update, QueueCompletion completion)
    {
        TextureBoundaryState[] boundaryStates = update.Values;
        for (int item = 0; item < boundaryStates.Length; item++)
            boundaryStates[item].SetCompletion(
                update.CompletionSources[item] < 0 ? completion : update.Completions[item]);
        StoreTextureStates(update.ResourceIndex, boundaryStates, update.BoundaryChanged);
    }

    private void StoreTextureStates(int resourceIndex, TextureBoundaryState[] boundaryStates, bool boundaryChanged)
    {
        ref FrameTexture frameTexture = ref CollectionsMarshal.AsSpan(_textures)[resourceIndex];
        if (frameTexture.Lifetime == RenderGraphResourceLifetime.Persistent &&
            frameTexture.Definition is { } resource)
        {
            resource.BoundaryStates = boundaryStates;
        }
        else
        {
            frameTexture.EntryBoundaryStates = boundaryStates;
            if (frameTexture.Ownership == RenderGraphResourceOwnership.CallerOwned && frameTexture.Definition is not null)
                frameTexture.Definition.BoundaryStates = boundaryStates;
            if (frameTexture.Lifetime == RenderGraphResourceLifetime.PerFrame &&
                frameTexture.Resource is not null)
            {
                _frame.Slot.TransientResources?.StoreCachedTextureStates(
                    frameTexture.AllocationIdentity,
                    frameTexture.Resource,
                    boundaryStates);
            }
        }
        if (!boundaryChanged && (frameTexture.Lifetime == RenderGraphResourceLifetime.Persistent ||
            frameTexture.Ownership == RenderGraphResourceOwnership.GraphOwned)) return;
        if (frameTexture.Definition is not null && (frameTexture.Lifetime == RenderGraphResourceLifetime.Persistent ||
            frameTexture.Ownership == RenderGraphResourceOwnership.CallerOwned))
            _frame.Graph.BoundaryChanged(frameTexture.Identity, GraphStoreKind.Textures);
        else _changedBoundaryResources.Add((GraphAccessTargetKind.Texture, resourceIndex));
    }

    private void BuildTextureStateUpdate(TextureStateUpdate update, ReadOnlySpan<TextureBoundaryState> current)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.StateUpdateConstruction);
        PrepareArray(ref update.Before, current.Length);
        _textureBoundaryScratch.Clear();
        for (int item = 0; item < current.Length; item++)
        {
            TextureBoundaryState state = current[item] with { ReadyAfter = null };
            update.Before[item] = state;
            _textureBoundaryScratch.Add(new(state, item));
        }
        for (int item = 0; item < update.Accesses.Length; item++)
        {
            FrameResourceAccess access = _accesses[update.Accesses[item]];
            ResourceContentState contents = access.ResultContents ?? access.Mode switch
            {
                GraphAccessMode.Read or GraphAccessMode.ReadWrite => ResourceContentState.Defined,
                GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete => ResourceContentState.Defined,
                _ => ResourceContentState.Undefined,
            };
            var endpoint = new TextureBoundaryState(access.TextureRange, access.Sync, access.Access,
                access.TextureLayout, contents, _passes[access.PassIndex].Queue);
            bool preserveContents = access.Mode == GraphAccessMode.Write &&
                access.Coverage == WriteCoverage.Partial && !access.ResultContents.HasValue;
            if (preserveContents)
            {
                _textureContentScratch.Clear();
                _textureContentScratch.AddRange(_textureBoundaryScratch);
            }
            var value = new TextureStateValue(endpoint, -1, access.PassIndex,
                access.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered);
            UpdateTextureBoundaryStates(_textureBoundaryScratch, value,
                access.Mode == GraphAccessMode.Read && !update.Acquired[item]);
            if (preserveContents)
            {
                foreach (TextureStateValue previous in _textureContentScratch)
                {
                    TextureSubresourceRange range = previous.State.Range;
                    if (previous.State.Contents != ResourceContentState.Defined ||
                        !Overlaps(range, access.TextureRange)) continue;
                    uint mip = Math.Max(range.FirstMipLevel, access.TextureRange.FirstMipLevel);
                    uint layer = Math.Max(range.FirstArrayLayer, access.TextureRange.FirstArrayLayer);
                    TextureSubresourceRange overlap = new(mip,
                        Math.Min(range.FirstMipLevel + range.MipLevelCount,
                            access.TextureRange.FirstMipLevel + access.TextureRange.MipLevelCount) - mip,
                        layer, Math.Min(range.FirstArrayLayer + range.ArrayLayerCount,
                            access.TextureRange.FirstArrayLayer + access.TextureRange.ArrayLayerCount) - layer,
                        range.Aspects & access.TextureRange.Aspects);
                    UpdateTextureBoundaryStates(_textureBoundaryScratch,
                        value with { State = endpoint with { Range = overlap, Contents = previous.State.Contents } }, reader: false);
                }
                _textureContentScratch.Clear();
            }
        }
        foreach (QueueRelease release in update.Releases)
            TransferTextureStates(_textureBoundaryScratch, release.TextureRange!.Value,
                _passes[_submissionPasses[update.Submission][0]].Queue!, ReleaseDestination(release.DestinationQueueType));
        update.Values = new TextureBoundaryState[_textureBoundaryScratch.Count];
        PrepareArray(ref update.CompletionSources, update.Values.Length);
        PrepareArray(ref update.Completions, update.Values.Length);
        _priorCompletionItems.Clear();
        for (int item = 0; item < update.Values.Length; item++)
        {
            TextureStateValue value = _textureBoundaryScratch[item];
            update.Values[item] = value.State;
            update.CompletionSources[item] = value.CompletionSource;
            if (value.CompletionSource >= 0) _priorCompletionItems.Add(item);
        }
        update.BoundaryChanged = current.Length != update.Values.Length;
        for (int item = 0; !update.BoundaryChanged && item < current.Length; item++)
            update.BoundaryChanged = !TextureBoundaryMatches(current[item], update.Values[item]);
        update.PriorCompletionItems = _priorCompletionItems.ToArray();
        update.Initialized = true;
    }

    private static void ReleaseBufferStates(List<BufferStateValue> states, Queue queue, Queue? releasedTo)
    {
        for (int index = 0; index < states.Count; index++)
            states[index] = new(states[index].State with
            {
                Sync = PipelineSync.None, Access = ResourceAccess.NoAccess,
                Queue = queue, ReleasedTo = releasedTo, ReadyAfter = null,
            }, -1);
    }

    private static void TransferTextureStates(List<TextureStateValue> states,
        TextureSubresourceRange range, Queue queue, Queue? releasedTo)
    {
        for (int index = states.Count - 1; index >= 0; index--)
        {
            TextureStateValue current = states[index];
            TextureSubresourceRange previous = current.State.Range;
            if (!Overlaps(previous, range)) continue;
            states.RemoveAt(index);
            Subtract(current, range, states);
            uint firstMip = Math.Max(previous.FirstMipLevel, range.FirstMipLevel);
            uint firstLayer = Math.Max(previous.FirstArrayLayer, range.FirstArrayLayer);
            var overlap = new TextureSubresourceRange(firstMip,
                Math.Min(previous.FirstMipLevel + previous.MipLevelCount, range.FirstMipLevel + range.MipLevelCount) - firstMip,
                firstLayer,
                Math.Min(previous.FirstArrayLayer + previous.ArrayLayerCount, range.FirstArrayLayer + range.ArrayLayerCount) - firstLayer,
                previous.Aspects & range.Aspects);
            states.Add(new(current.State with
            {
                Range = overlap, Sync = PipelineSync.None, Access = ResourceAccess.NoAccess,
                Layout = TextureLayout.General, Queue = queue, ReleasedTo = releasedTo,
                ReleaseRange = releasedTo is null ? null : range, ReadyAfter = null,
            }, -1));
        }
    }

    private void PrepareLateReleaseState(Queue queue, List<PendingQueueRelease> releases)
    {
        _lateStateResources.Clear();
        _lateBufferStateCount = _lateTextureStateCount = 0;
        foreach (PendingQueueRelease release in releases)
            if (_resourceRows.TryGetValue(release.Resource, out var key)) _lateStateResources.Add(key);
        foreach (var key in _lateStateResources)
        {
            if (key.Kind == GraphAccessTargetKind.Buffer)
            {
                if (_lateBufferStateCount == _lateBufferStateUpdates.Count) _lateBufferStateUpdates.Add(new());
                BufferStateUpdate update = _lateBufferStateUpdates[_lateBufferStateCount++];
                update.ResourceIndex = key.Index;
                FrameBuffer buffer = _buffers[key.Index];
                ReadOnlySpan<BufferBoundaryState> current = BufferEntryStates(buffer);
                _bufferBoundaryScratch.Clear();
                for (int item = 0; item < current.Length; item++)
                    _bufferBoundaryScratch.Add(new(current[item] with { ReadyAfter = null }, item));
                foreach (PendingQueueRelease release in releases)
                    if (ReferenceEquals(release.Resource, buffer.Resource) && release.AcquireFrom.HasValue)
                        ReleaseBufferStates(_bufferBoundaryScratch, queue, null);
                foreach (PendingQueueRelease release in releases)
                    if (ReferenceEquals(release.Resource, buffer.Resource) && release.Release)
                        ReleaseBufferStates(_bufferBoundaryScratch, queue, release.DestinationQueue);
                BufferBoundaryState[] values = new BufferBoundaryState[_bufferBoundaryScratch.Count];
                PrepareArray(ref update.CompletionSources, values.Length);
                PrepareArray(ref update.Completions, values.Length);
                for (int item = 0; item < values.Length; item++)
                {
                    BufferStateValue value = _bufferBoundaryScratch[item];
                    values[item] = value.State;
                    int source = value.CompletionSource;
                    update.CompletionSources[item] = source;
                    update.Completions[item] = source < 0 ? null : current[source].ReadyAfter;
                }
                update.Boundaries = new(values, BuildBufferCompletionItems(update.CompletionSources));
            }
            else if (key.Kind == GraphAccessTargetKind.Texture)
            {
                if (_lateTextureStateCount == _lateTextureStateUpdates.Count) _lateTextureStateUpdates.Add(new());
                TextureStateUpdate update = _lateTextureStateUpdates[_lateTextureStateCount++];
                update.ResourceIndex = key.Index;
                FrameTexture texture = _textures[key.Index];
                ReadOnlySpan<TextureBoundaryState> current = TextureEntryStates(texture);
                _textureBoundaryScratch.Clear();
                for (int item = 0; item < current.Length; item++)
                    _textureBoundaryScratch.Add(new(current[item] with { ReadyAfter = null }, item));
                foreach (PendingQueueRelease release in releases)
                    if (ReferenceEquals(release.Resource, texture.Resource) && release.AcquireFrom.HasValue)
                        TransferTextureStates(_textureBoundaryScratch, release.TextureRange!.Value, queue, null);
                foreach (PendingQueueRelease release in releases)
                    if (ReferenceEquals(release.Resource, texture.Resource) && release.Release)
                        TransferTextureStates(_textureBoundaryScratch, release.TextureRange!.Value, queue, release.DestinationQueue);
                update.Values = new TextureBoundaryState[_textureBoundaryScratch.Count];
                PrepareArray(ref update.CompletionSources, update.Values.Length);
                PrepareArray(ref update.Completions, update.Values.Length);
                for (int item = 0; item < update.Values.Length; item++)
                {
                    TextureStateValue value = _textureBoundaryScratch[item];
                    update.Values[item] = value.State;
                    int source = value.CompletionSource;
                    update.CompletionSources[item] = source;
                    update.Completions[item] = source < 0 ? null : current[source].ReadyAfter;
                }
            }
        }
    }

    private void CommitLateReleaseState(QueueCompletion completion)
    {
        for (int index = 0; index < _lateBufferStateCount; index++) CommitBuffer(_lateBufferStateUpdates[index], completion);
        for (int index = 0; index < _lateTextureStateCount; index++) CommitTexture(_lateTextureStateUpdates[index], completion);
    }

    private void BuildQueryStateUpdate(QueryStateUpdate update, ReadOnlySpan<QueryBoundaryState> current)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.StateUpdateConstruction);
        PrepareArray(ref update.Before, current.Length);
        _queryBoundaryScratch.Clear();
        for (int item = 0; item < current.Length; item++)
        {
            QueryBoundaryState state = current[item] with { ReadyAfter = null };
            update.Before[item] = state;
            _queryBoundaryScratch.Add(new(state, item));
        }
        foreach (int index in update.Accesses)
        {
            FrameResourceAccess access = _accesses[index];
            ResourceContentState contents = access.ResultContents ?? access.Mode switch
            {
                GraphAccessMode.Read or GraphAccessMode.ReadWrite => ResourceContentState.Defined,
                GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete => ResourceContentState.Defined,
                _ => ResourceContentState.Undefined,
            };
            bool preserveContents = access.Mode == GraphAccessMode.Write &&
                access.Coverage == WriteCoverage.Partial && !access.ResultContents.HasValue;
            if (preserveContents)
            {
                _queryContentScratch.Clear();
                _queryContentScratch.AddRange(_queryBoundaryScratch);
            }
            var endpoint = new QueryBoundaryState(access.QueryRange, contents, _passes[access.PassIndex].Queue, null);
            UpdateQueryEndpoints(_queryBoundaryScratch,
                new(endpoint, -1),
                access.Mode == GraphAccessMode.Read);
            if (preserveContents)
            {
                foreach (QueryStateValue previous in _queryContentScratch)
                {
                    if (previous.State.Contents != ResourceContentState.Defined) continue;
                    uint start = Math.Max(previous.State.Range.FirstQuery, access.QueryRange.FirstQuery);
                    uint end = Math.Min(previous.State.Range.FirstQuery + previous.State.Range.QueryCount,
                        access.QueryRange.FirstQuery + access.QueryRange.QueryCount);
                    if (start >= end) continue;
                    UpdateQueryEndpoints(_queryBoundaryScratch,
                        new(endpoint with { Range = new(start, end - start), Contents = previous.State.Contents }, -1), reader: false);
                }
                _queryContentScratch.Clear();
            }
        }
        update.Values = new QueryBoundaryState[_queryBoundaryScratch.Count];
        PrepareArray(ref update.CompletionSources, update.Values.Length);
        PrepareArray(ref update.Completions, update.Values.Length);
        _priorCompletionItems.Clear();
        for (int item = 0; item < update.Values.Length; item++)
        {
            QueryStateValue value = _queryBoundaryScratch[item];
            update.Values[item] = value.State;
            update.CompletionSources[item] = value.CompletionSource;
            if (value.CompletionSource >= 0) _priorCompletionItems.Add(item);
        }
        update.PriorCompletionItems = _priorCompletionItems.ToArray();
        update.Initialized = true;
    }

    private void BuildShaderTableStateUpdate(ShaderTableStateUpdate update,
        ReadOnlySpan<RayTracingShaderTableBoundaryState> current)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.StateUpdateConstruction);
        PrepareArray(ref update.Before, current.Length);
        _shaderTableBoundaryScratch.Clear();
        for (int item = 0; item < current.Length; item++)
        {
            RayTracingShaderTableBoundaryState state = current[item] with { ReadyAfter = null };
            update.Before[item] = state;
            _shaderTableBoundaryScratch.Add(new(state, item));
        }
        foreach (int index in update.Accesses)
        {
            FrameResourceAccess access = _accesses[index];
            ResourceContentState contents = access.ResultContents ?? access.Mode switch
            {
                GraphAccessMode.ReadWrite => ResourceContentState.Defined,
                GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete => ResourceContentState.Defined,
                _ => FindShaderTableContents(CollectionsMarshal.AsSpan(_shaderTableBoundaryScratch)),
            };
            Queue queue = _passes[access.PassIndex].Queue!;
            if (access.Mode != GraphAccessMode.Read) _shaderTableBoundaryScratch.Clear();
            else
                for (int item = _shaderTableBoundaryScratch.Count - 1; item >= 0; item--)
                    if (ReferenceEquals(_shaderTableBoundaryScratch[item].State.Queue, queue))
                        _shaderTableBoundaryScratch.RemoveAt(item);
            _shaderTableBoundaryScratch.Add(new(new RayTracingShaderTableBoundaryState(contents, queue, null), -1));
        }
        update.Values = new RayTracingShaderTableBoundaryState[_shaderTableBoundaryScratch.Count];
        PrepareArray(ref update.CompletionSources, update.Values.Length);
        PrepareArray(ref update.Completions, update.Values.Length);
        _priorCompletionItems.Clear();
        for (int item = 0; item < update.Values.Length; item++)
        {
            ShaderTableStateValue value = _shaderTableBoundaryScratch[item];
            update.Values[item] = value.State;
            update.CompletionSources[item] = value.CompletionSource;
            if (value.CompletionSource >= 0) _priorCompletionItems.Add(item);
        }
        update.PriorCompletionItems = _priorCompletionItems.ToArray();
        update.Initialized = true;
    }

    private static ResourceContentState FindShaderTableContents(ReadOnlySpan<ShaderTableStateValue> values)
    {
        if (values.IsEmpty) return ResourceContentState.Undefined;
        ResourceContentState contents = values[0].State.Contents;
        foreach (ShaderTableStateValue value in values)
            DevelopmentCheck.Require(value.State.Contents == contents,
                "RayTracingShaderTable reader boundary states disagree about contents.");
        return contents;
    }

    private static void UpdateBufferBoundaryStates(
        List<BufferStateValue> current,
        in BufferStateValue update,
        bool reader)
    {
        ulong updateStart = update.State.Range.Offset;
        ulong updateEnd = checked(updateStart + update.State.Range.Size);
        bool replaced = false;
        for (int index = current.Count - 1; index >= 0; index--)
        {
            BufferStateValue value = current[index];
            BufferBoundaryState existing = value.State;
            ulong start = existing.Range.Offset;
            ulong end = checked(start + existing.Range.Size);
            if (end <= updateStart || start >= updateEnd)
                continue;
            if (reader && !ResourceAccessRules.Writes(existing.Access) &&
                existing.Queue is not null && update.State.Queue is not null &&
                !ReferenceEquals(existing.Queue, update.State.Queue))
                continue;
            if (!replaced && existing.Range == update.State.Range)
            {
                current[index] = update;
                replaced = true;
                continue;
            }
            current.RemoveAt(index);
            // A later reader supersedes only the bytes it reads. Keep the other
            // content ranges and their completion, including on the same queue.
            if (start < updateStart)
                current.Add(value with { State = existing with { Range = new BufferRange(start, updateStart - start) } });
            if (end > updateEnd)
                current.Add(value with { State = existing with { Range = new BufferRange(updateEnd, end - updateEnd) } });
        }
        if (!replaced)
            current.Add(update);
    }

    private static void PublishBufferPhysicalState(
        Span<BufferStateValue> boundaryStates,
        in BufferBoundaryState endpoint,
        bool retainDistinctQueueReaders,
        bool publishOwnership)
    {
        // Buffer barriers advance Sync/Access for the whole physical resource. A QueueAcquire is
        // whole-resource as well, so its destination Queue and completion own every byte even when
        // the consuming access covers only a smaller logical range. Content definition remains
        // range-scoped in UpdateBufferBoundaryStates.
        for (int index = 0; index < boundaryStates.Length; index++)
        {
            BufferStateValue value = boundaryStates[index];
            BufferBoundaryState state = value.State;
            bool distinctQueueReader = retainDistinctQueueReaders &&
                !ResourceAccessRules.Writes(state.Access) &&
                state.Queue is not null &&
                endpoint.Queue is not null &&
                !ReferenceEquals(state.Queue, endpoint.Queue);
            if (distinctQueueReader) continue;

            boundaryStates[index] = value with
            {
                State = state with
                {
                    Sync = endpoint.Sync,
                    Access = endpoint.Access,
                    Queue = publishOwnership ? endpoint.Queue : state.Queue,
                    ReleasedTo = publishOwnership ? null : state.ReleasedTo,
                },
                CompletionSource = publishOwnership ? -1 : value.CompletionSource,
            };
        }
    }

    private bool HasIncomingBufferAcquire(int pass, Buffer? buffer)
    {
        if (buffer is null) return false;
        foreach (QueueAcquire acquire in _acquires[pass])
            if (ReferenceEquals(acquire.Resource, buffer))
                return true;
        return false;
    }

    private bool HasIncomingTextureAcquire(
        int pass,
        Texture? texture,
        in TextureSubresourceRange range)
    {
        if (texture is null) return false;
        foreach (QueueAcquire acquire in _acquires[pass])
        {
            if (!ReferenceEquals(acquire.Resource, texture)) continue;
            if (acquire.TextureRange is not { } acquiredRange ||
                Overlaps(acquiredRange, range))
                return true;
        }
        return false;
    }

    private static void UpdateQueryEndpoints(List<QueryStateValue> current, in QueryStateValue update, bool reader)
    {
        uint updateStart = update.State.Range.FirstQuery;
        uint updateEnd = checked(updateStart + update.State.Range.QueryCount);
        for (int item = current.Count - 1; item >= 0; item--)
        {
            QueryStateValue value = current[item];
            QueryBoundaryState existing = value.State;
            uint start = existing.Range.FirstQuery, end = checked(start + existing.Range.QueryCount);
            if (end <= updateStart || start >= updateEnd ||
                reader && !ReferenceEquals(existing.Queue, update.State.Queue)) continue;
            current.RemoveAt(item);
            if (start < updateStart)
                current.Add(value with { State = existing with { Range = new QueryRange(start, updateStart - start) } });
            if (end > updateEnd)
                current.Add(value with { State = existing with { Range = new QueryRange(updateEnd, end - updateEnd) } });
        }
        current.Add(update);
        current.Sort(static (left, right) => left.State.Range.FirstQuery.CompareTo(right.State.Range.FirstQuery));
    }

    private void UpdateTextureBoundaryStates(
        List<TextureStateValue> current, in TextureStateValue update, bool reader)
    {
        _textureWriteScratch.Clear();
        _textureWriteScratch.Add(update);
        int count = current.Count;
        for (int index = count - 1; index >= 0; index--)
        {
            TextureStateValue existing = current[index];
            if (!Overlaps(existing.State.Range, update.State.Range)) continue;
            if (reader && !ResourceAccessRules.Writes(existing.State.Access) &&
                !ReferenceEquals(existing.State.Queue, update.State.Queue)) continue;
            current.RemoveAt(index);
            Subtract(existing, update.State.Range, current);
            bool samePass = update.LastPass >= 0 && existing.LastPass == update.LastPass;
            bool writeChain = existing.MergeableWrites && update.MergeableWrites &&
                existing.State.Access == update.State.Access;
            if ((!samePass && !writeChain) || existing.State.Layout != update.State.Layout ||
                !ReferenceEquals(existing.State.Queue, update.State.Queue)) continue;

            // No barrier separates these uses. Preserve every shader stage that can
            // still write, including when only part of the texture overlaps.
            for (int part = _textureWriteScratch.Count - 1; part >= 0; part--)
            {
                TextureStateValue value = _textureWriteScratch[part];
                TextureSubresourceRange range = value.State.Range;
                TextureSubresourceRange previous = existing.State.Range;
                if (!Overlaps(range, previous)) continue;
                _textureWriteScratch.RemoveAt(part);
                Subtract(value, previous, _textureWriteScratch);
                uint firstMip = Math.Max(range.FirstMipLevel, previous.FirstMipLevel);
                uint firstLayer = Math.Max(range.FirstArrayLayer, previous.FirstArrayLayer);
                var overlap = new TextureSubresourceRange(firstMip,
                    Math.Min(range.FirstMipLevel + range.MipLevelCount,
                        previous.FirstMipLevel + previous.MipLevelCount) - firstMip,
                    firstLayer,
                    Math.Min(range.FirstArrayLayer + range.ArrayLayerCount,
                        previous.FirstArrayLayer + previous.ArrayLayerCount) - firstLayer,
                    range.Aspects & previous.Aspects);
                _textureWriteScratch.Add(value with
                {
                    State = value.State with
                    {
                        Range = overlap,
                        Sync = value.State.Sync | existing.State.Sync,
                        Access = value.State.Access | existing.State.Access,
                    },
                    MergeableWrites = value.MergeableWrites && existing.MergeableWrites,
                });
            }
        }
        current.AddRange(_textureWriteScratch);
        _textureWriteScratch.Clear();
        current.Sort(static (left, right) =>
        {
            int aspect = ((uint)left.State.Range.Aspects).CompareTo((uint)right.State.Range.Aspects);
            if (aspect != 0) return aspect;
            int layer = left.State.Range.FirstArrayLayer.CompareTo(right.State.Range.FirstArrayLayer);
            return layer != 0 ? layer : left.State.Range.FirstMipLevel.CompareTo(right.State.Range.FirstMipLevel);
        });
    }

    private static void Subtract(
        in TextureStateValue existing,
        in TextureSubresourceRange removed,
        List<TextureStateValue> destination)
    {
        TextureStateValue endpoint = existing;
        TextureSubresourceRange source = existing.State.Range;
        TextureAspects untouchedAspects = source.Aspects & ~removed.Aspects;
        AddRange(source.FirstMipLevel, source.MipLevelCount,
            source.FirstArrayLayer, source.ArrayLayerCount, untouchedAspects);

        TextureAspects overlappingAspects = source.Aspects & removed.Aspects;
        if (overlappingAspects == TextureAspects.None) return;

        uint sourceMipEnd = checked(source.FirstMipLevel + source.MipLevelCount);
        uint sourceLayerEnd = checked(source.FirstArrayLayer + source.ArrayLayerCount);
        uint removedMipStart = Math.Max(source.FirstMipLevel, removed.FirstMipLevel);
        uint removedMipEnd = Math.Min(sourceMipEnd,
            checked(removed.FirstMipLevel + removed.MipLevelCount));
        uint removedLayerStart = Math.Max(source.FirstArrayLayer, removed.FirstArrayLayer);
        uint removedLayerEnd = Math.Min(sourceLayerEnd,
            checked(removed.FirstArrayLayer + removed.ArrayLayerCount));

        AddRange(source.FirstMipLevel, source.MipLevelCount,
            source.FirstArrayLayer, removedLayerStart - source.FirstArrayLayer,
            overlappingAspects);
        AddRange(source.FirstMipLevel, source.MipLevelCount,
            removedLayerEnd, sourceLayerEnd - removedLayerEnd,
            overlappingAspects);
        AddRange(source.FirstMipLevel, removedMipStart - source.FirstMipLevel,
            removedLayerStart, removedLayerEnd - removedLayerStart,
            overlappingAspects);
        AddRange(removedMipEnd, sourceMipEnd - removedMipEnd,
            removedLayerStart, removedLayerEnd - removedLayerStart,
            overlappingAspects);
        return;

        void AddRange(
            uint firstMip,
            uint mipCount,
            uint firstLayer,
            uint layerCount,
            TextureAspects aspects)
        {
            if (mipCount == 0 || layerCount == 0 || aspects == TextureAspects.None) return;
            destination.Add(endpoint with
            {
                State = endpoint.State with { Range = new TextureSubresourceRange(
                    firstMip, mipCount, firstLayer, layerCount, aspects) },
            });
        }
    }

    private static BufferContentScope FindBufferContentScope(
        ReadOnlySpan<BufferStateValue> boundaryStates,
        in BufferRange range)
    {
        foreach (BufferStateValue endpoint in boundaryStates)
            if (Contains(endpoint.State.Range, range)) return endpoint.State.ContentScope;
        return BufferContentScope.FullRange;
    }

    private static bool Overlaps(
        in TextureSubresourceRange left,
        in TextureSubresourceRange right)
    {
        bool mip = left.FirstMipLevel < right.FirstMipLevel + right.MipLevelCount &&
                   right.FirstMipLevel < left.FirstMipLevel + left.MipLevelCount;
        bool layer = left.FirstArrayLayer < right.FirstArrayLayer + right.ArrayLayerCount &&
                     right.FirstArrayLayer < left.FirstArrayLayer + left.ArrayLayerCount;
        return mip && layer && (left.Aspects & right.Aspects) != 0;
    }
}

