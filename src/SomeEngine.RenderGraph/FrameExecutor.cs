namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly RenderGraphFrameState _frame;
    private FrameBuffer[] _buffers = [];
    private FrameTexture[] _textures = [];
    private FrameQueryPool[] _queryPools = [];
    private FrameRayTracingShaderTable[] _shaderTables = [];
    private FrameView[] _views = [];
    private FramePass[] _passes = [];
    private FrameResourceAccess[] _accesses = [];
    private readonly Dictionary<GraphIdentity, int> _bufferIndices = [];
    private readonly Dictionary<GraphIdentity, int> _textureIndices = [];
    private readonly Dictionary<GraphIdentity, int> _queryPoolIndices = [];
    private readonly Dictionary<GraphIdentity, int> _shaderTableIndices = [];
    private readonly Dictionary<GraphIdentity, int> _viewIndices = [];
    private readonly Dictionary<GraphIdentity, int> _passIndices = [];
    private readonly Dictionary<GraphIdentity, int> _accessIndices = [];
    private readonly List<(long Key, FramePass Pass)> _passRows = [];
    private readonly Dictionary<int, int> _extensionCounts = [];
    private readonly List<int> _resourceAccessScratch = [];
    private readonly Stack<int> _livenessStack = new();
    private readonly PriorityQueue<int, int> _cycleReady = new();
    private readonly List<int> _shaderReaderScratch = [];
    private ulong[] _bufferCoordinateScratch = [];
    private uint[] _queryCoordinateScratch = [];
    private int[] _writerScratch = [];
    private ResourceContentState[] _contentsScratch = [];
    private List<int>[] _readerScratch = [];
    private TextureLayout?[] _layoutScratch = [];
    private int[] _cycleIndegree = [];
    private int[] _criticalRanks = [];
    private int[] _scheduleIndegrees = [];
    private int[] _criticalTopological = [];
    private readonly List<int> _scheduleReady = [];
    private readonly PriorityQueue<int, int> _criticalReady = new();
    private readonly Dictionary<Queue, int> _queueTime =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Queue, int> _queueLastPass =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<Queue> _queueCandidateScratch = [];
    private List<int>[] _valuePredecessors = [];
    private List<int>[] _predecessors = [];
    private List<int>[] _successors = [];
    private List<int>[] _passAccesses = [];
    private bool[] _live = [];
    private int[] _schedule = [];
    private int[] _scheduledPosition = [];
    private int[] _sameQueuePredecessor = [];
    private int[][] _startFrontiers = [];
    private int[][] _endFrontiers = [];
    private readonly Dictionary<Queue, int> _queueLanes = new(ReferenceEqualityComparer.Instance);
    private int _queueLaneCount;
    private ulong _stableExecutionVersion;
    private FrameSubmissionMode _stableExecutionSubmissionMode;
    private RenderGraphDebugOptions _stableExecutionDebug;
    private int _stableExecutionPreparations;
    private bool _stableExecutionEligible;
    private bool _stableResourceCacheReady;
    private bool _reuseStableExecution;
    private int _stableWaveCount;
    private bool _stableSingleWaveParallelRecording;
    private int _stableCoarseBatchCount;

    internal FrameExecutor(RenderGraphFrameState frame)
    {
        _frame = frame;
    }

    internal void Reset()
    {
        _stableExecutionEligible = IsStableExecutionEligible();
        bool sameExecution = _stableExecutionEligible &&
            _stableExecutionVersion == _frame.Graph.StructureVersion &&
            _stableExecutionSubmissionMode == _frame.Options.SubmissionMode &&
            _stableExecutionDebug == _frame.Options.Debug;
        FrameTransientResourceAllocator? transientResources = _frame.Slot.TransientResources;
        if (_stableExecutionEligible && !sameExecution)
            transientResources?.InvalidatePlacementHistory();
        if (!sameExecution)
        {
            _stableExecutionVersion = _stableExecutionEligible
                ? _frame.Graph.StructureVersion
                : 0;
            _stableExecutionSubmissionMode = _frame.Options.SubmissionMode;
            _stableExecutionDebug = _frame.Options.Debug;
            _stableExecutionPreparations = 0;
            _stableWaveCount = 0;
            _stableSingleWaveParallelRecording = false;
            _stableCoarseBatchCount = 0;
            InvalidateRasterRenderingCache();
        }
        _stableResourceCacheReady = _stableExecutionEligible &&
            transientResources?.CanCacheResources == true;
        if (!_stableResourceCacheReady)
            _stableExecutionPreparations = 0;
        _reuseStableExecution = sameExecution &&
            _stableResourceCacheReady &&
            _stableExecutionPreparations >= 2;
        if (!_reuseStableExecution)
        {
            _bufferIndices.Clear();
            _textureIndices.Clear();
            _queryPoolIndices.Clear();
            _shaderTableIndices.Clear();
            _viewIndices.Clear();
            _passIndices.Clear();
            _accessIndices.Clear();
            _queueLanes.Clear();
            _queueLaneCount = 0;
        }
        _submittedCompletions.Clear();
        _lateReleases.Clear();
    }

    private bool IsStableExecutionEligible()
    {
        if (_frame.DynamicBuffers.Count != 0 ||
            _frame.DynamicTextures.Count != 0 ||
            _frame.DynamicViews.Count != 0 ||
            _frame.DynamicPasses.Count != 0 ||
            _frame.DynamicAccesses.Count != 0 ||
            _frame.DynamicOrders.Count != 0 ||
            _frame.EnabledOverrides.Count != 0 ||
            _frame.BufferRangeOverrides.Count != 0 ||
            _frame.TextureRangeOverrides.Count != 0 ||
            _frame.BufferBindings.Count != 0 ||
            _frame.TextureBindings.Count != 0 ||
            _frame.SwapchainImages.Count != 0 ||
            _frame.DynamicRenderingRegions.Count != 0 ||
            _frame.DynamicColorAttachments.Count != 0 ||
            _frame.DynamicDepthAttachments.Count != 0)
            return false;

        GraphStructure structure = _frame.Graph.StructureIndex.Structure;
        if (structure.QueryPools.Count != 0 || structure.ShaderTables.Count != 0)
            return false;
        foreach (GraphBuffer buffer in structure.Buffers.Rows)
            if (buffer.Ownership != RenderGraphResourceOwnership.GraphOwned ||
                buffer.Lifetime != RenderGraphResourceLifetime.PerFrame)
                return false;
        foreach (GraphTexture texture in structure.Textures.Rows)
            if (texture.Ownership != RenderGraphResourceOwnership.GraphOwned ||
                texture.Lifetime != RenderGraphResourceLifetime.PerFrame)
                return false;
        return true;
    }

    internal int Execute(Span<QueueCompletion> destination)
    {
        if (!_reuseStableExecution) BuildRows();
        if (_passes.Length == 0)
        {
            PublishDiagnostics();
            return 0;
        }
        if (!_reuseStableExecution)
        {
            ResolveValues();
            ResolveLiveness();
            ResolveHazards();
            ResolveSchedule();
            ResolveFrontiersAndLifetimes();
            Materialize();
            ResolveSynchronization();
        }
        PublishDiagnostics();
        int result = RecordAndSubmit(destination);
        if (_stableExecutionEligible && _stableResourceCacheReady && !_reuseStableExecution)
            _stableExecutionPreparations++;
        return result;
    }

    private void BuildRows()
    {
        GraphStructureIndex structureIndex = _frame.Graph.StructureIndex;
        int staticBufferCount = structureIndex.Structure.Buffers.Count;
        int staticTextureCount = structureIndex.Structure.Textures.Count;
        int staticQueryPoolCount = structureIndex.Structure.QueryPools.Count;
        int staticShaderTableCount = structureIndex.Structure.ShaderTables.Count;
        int staticViewCount = structureIndex.Structure.Views.Count;
        int staticPassCount = structureIndex.Structure.Passes.Count;
        int staticAccessCount = structureIndex.Structure.Accesses.Count;

        if (staticBufferCount == 0 && _frame.DynamicBuffers.Count == 0 &&
            staticTextureCount == 0 && _frame.DynamicTextures.Count == 0 &&
            staticQueryPoolCount == 0 && staticShaderTableCount == 0 &&
            staticViewCount == 0 && _frame.DynamicViews.Count == 0 &&
            staticPassCount == 0 && _frame.DynamicPasses.Count == 0 &&
            staticAccessCount == 0 && _frame.DynamicAccesses.Count == 0)
        {
            _buffers = Array.Empty<FrameBuffer>();
            _textures = Array.Empty<FrameTexture>();
            _queryPools = Array.Empty<FrameQueryPool>();
            _shaderTables = Array.Empty<FrameRayTracingShaderTable>();
            _views = Array.Empty<FrameView>();
            _passes = Array.Empty<FramePass>();
            _accesses = Array.Empty<FrameResourceAccess>();
            _valuePredecessors = Array.Empty<List<int>>();
            _predecessors = Array.Empty<List<int>>();
            _successors = Array.Empty<List<int>>();
            _passAccesses = Array.Empty<List<int>>();
            _live = Array.Empty<bool>();
            return;
        }

        PrepareArray(ref _buffers, staticBufferCount + _frame.DynamicBuffers.Count);
        for (int i = 0; i < staticBufferCount; i++)
        {
            GraphBuffer source = structureIndex.Structure.Buffers.Rows[i];
            GraphIdentity identity = structureIndex.BufferIds[i];
            Buffer? resource = source.PersistentResource ?? source.RegisteredResource;
            BufferBoundaryState[]? boundaryStates = source.BoundaryStates;
            if (source.Ownership == RenderGraphResourceOwnership.CallerOwned &&
                source.Lifetime == RenderGraphResourceLifetime.PerFrame &&
                resource is null)
            {
                if (!_frame.BufferBindings.TryGetValue(identity, out var binding))
                    throw new InvalidOperationException($"External Buffer '{source.Label}' is not bound.");
                resource = binding.Resource;
                boundaryStates = binding.BoundaryStates;
            }
            _buffers[i] = new FrameBuffer
            {
                Identity = identity,
                Definition = source,
                Description = source.Description,
                MemoryType = source.MemoryType,
                Ownership = source.Ownership,
                Lifetime = source.Lifetime,
                Requirements = source.Requirements,
                Resource = resource,
                EntryBoundaryStates = boundaryStates,
                FirstUse = int.MaxValue,
                LastUse = -1,
            };
            _bufferIndices.Add(identity, i);
        }
        for (int i = 0; i < _frame.DynamicBuffers.Count; i++)
        {
            FrameBuffer source = _frame.DynamicBuffers[i];
            int destination = staticBufferCount + i;
            _buffers[destination] = source;
            _bufferIndices.Add(source.Identity, destination);
        }

        PrepareArray(ref _textures, staticTextureCount + _frame.DynamicTextures.Count);
        for (int i = 0; i < staticTextureCount; i++)
        {
            GraphTexture source = structureIndex.Structure.Textures.Rows[i];
            GraphIdentity identity = structureIndex.TextureIds[i];
            Texture? resource = source.PersistentResource ?? source.RegisteredResource;
            TextureBoundaryState[]? boundaryStates = source.BoundaryStates;
            if (source.Ownership == RenderGraphResourceOwnership.CallerOwned &&
                source.Lifetime == RenderGraphResourceLifetime.PerFrame &&
                resource is null)
            {
                if (!_frame.TextureBindings.TryGetValue(identity, out var binding))
                    throw new InvalidOperationException($"External Texture '{source.Label}' is not bound.");
                resource = binding.Resource;
                boundaryStates = binding.BoundaryStates;
            }
            _textures[i] = new FrameTexture
            {
                Identity = identity,
                Definition = source,
                Dimension = source.Dimension,
                Width = source.Width,
                Height = source.Height,
                Depth = source.Depth,
                MipLevelCount = source.MipLevelCount,
                ArrayLayerCount = source.ArrayLayerCount,
                SampleCount = source.SampleCount,
                Format = source.Format,
                Usages = source.Usages,
                PermittedViewFormats = source.PermittedViewFormats,
                Label = source.Label,
                NodePlacement = source.NodePlacement,
                Ownership = source.Ownership,
                Lifetime = source.Lifetime,
                Requirements = source.Requirements,
                Resource = resource,
                EntryBoundaryStates = boundaryStates,
                FirstUse = int.MaxValue,
                LastUse = -1,
            };
            _textureIndices.Add(identity, i);
        }
        for (int i = 0; i < _frame.DynamicTextures.Count; i++)
        {
            FrameTexture source = _frame.DynamicTextures[i];
            int destination = staticTextureCount + i;
            _textures[destination] = source;
            _textureIndices.Add(source.Identity, destination);
        }

        PrepareArray(ref _queryPools, staticQueryPoolCount);
        for (int i = 0; i < staticQueryPoolCount; i++)
        {
            GraphQueryPool source = structureIndex.Structure.QueryPools.Rows[i];
            GraphIdentity identity = structureIndex.QueryPoolIds[i];
            _queryPools[i] = new FrameQueryPool
            {
                Identity = identity,
                Definition = source,
                Resource = source.Resource,
                EntryBoundaryStates = source.BoundaryStates,
            };
            _queryPoolIndices.Add(identity, i);
        }

        PrepareArray(ref _shaderTables, staticShaderTableCount);
        for (int i = 0; i < staticShaderTableCount; i++)
        {
            GraphRayTracingShaderTable source = structureIndex.Structure.ShaderTables.Rows[i];
            GraphIdentity identity = structureIndex.ShaderTableIds[i];
            _shaderTables[i] = new FrameRayTracingShaderTable
            {
                Identity = identity,
                Definition = source,
                Resource = source.Resource,
                Inventory = source.Inventory,
                EntryBoundaryStates = source.BoundaryStates,
            };
            _shaderTableIndices.Add(identity, i);
        }

        PrepareArray(ref _views, staticViewCount + _frame.DynamicViews.Count);
        for (int i = 0; i < staticViewCount; i++)
        {
            GraphView source = structureIndex.Structure.Views.Rows[i];
            GraphIdentity identity = structureIndex.ViewIds[i];
            _views[i] = new FrameView
            {
                Identity = identity,
                Definition = source,
                Kind = source.Kind,
                Buffer = source.Buffer,
                Texture = source.Texture,
                AdditionalBuffer = source.AdditionalBuffer,
                BufferRange = source.BufferRange,
                TextureRange = source.TextureRange,
                BufferFormat = source.BufferFormat,
                TextureFormat = source.TextureFormat,
                StructureStride = source.StructureStride,
                CounterOffset = source.CounterOffset,
                Dimension = source.Dimension,
                ReadOnlyDepth = source.ReadOnlyDepth,
                ReadOnlyStencil = source.ReadOnlyStencil,
                Label = source.Label,
                View = source.PersistentView,
            };
            _viewIndices.Add(identity, i);
        }
        for (int i = 0; i < _frame.DynamicViews.Count; i++)
        {
            FrameView source = _frame.DynamicViews[i];
            int destination = staticViewCount + i;
            _views[destination] = source;
            _viewIndices.Add(source.Identity, destination);
        }

        _passRows.Clear();
        _passRows.EnsureCapacity(staticPassCount + _frame.DynamicPasses.Count);
        for (int i = 0; i < staticPassCount; i++)
        {
            GraphPass source = structureIndex.Structure.Passes.Rows[i];
            GraphIdentity identity = structureIndex.PassIds[i];
            bool enabled = !_frame.EnabledOverrides.TryGetValue(identity, out bool value) || value;
            _passRows.Add((checked((long)i << 32), new FramePass
            {
                Identity = identity,
                Definition = source,
                Label = source.Label,
                Kind = source.Kind,
                QueuePolicy = source.Queue,
                Options = source.Options,
                Pipeline = source.Pipeline,
                ParameterLayout = source.ParameterLayout,
                ParameterOrdinaryData = source.ParameterOrdinaryData,
                ParameterBindings = source.ParameterBindings,
                PersistentBindings = source.PersistentBindings,
                DeclarationOrdinal = i,
                Enabled = enabled,
                PersistentCallbacks = source.CallbackStorage,
                ExtensionPointIndex = -1,
            }));
        }

        _extensionCounts.Clear();
        for (int i = 0; i < _frame.DynamicPasses.Count; i++)
        {
            FramePass source = _frame.DynamicPasses[i];
            int insertion = source.ExtensionPointIndex >= 0
                ? structureIndex.Structure.ExtensionPoints.Rows[source.ExtensionPointIndex].DeclarationOrdinal
                : staticPassCount;
            _extensionCounts.TryGetValue(insertion, out int ordinal);
            _extensionCounts[insertion] = ordinal + 1;
            long key = checked(((long)insertion << 32) + ordinal + 1);
            source.DeclarationOrdinal = staticPassCount + i;
            _passRows.Add((key, source));
        }
        _passRows.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        PrepareArray(ref _passes, _passRows.Count);
        for (int i = 0; i < _passes.Length; i++)
        {
            FramePass pass = _passRows[i].Pass;
            pass.DeclarationOrdinal = i;
            _passes[i] = pass;
            _passIndices.Add(pass.Identity, i);
        }

        PrepareArray(ref _accesses, staticAccessCount + _frame.DynamicAccesses.Count);
        for (int i = 0; i < staticAccessCount; i++)
        {
            PassResourceAccess source = structureIndex.Structure.Accesses.Rows[i];
            GraphIdentity identity = structureIndex.AccessIds[i];
            BufferRange bufferRange = source.BufferRange;
            TextureSubresourceRange textureRange = source.TextureRange;
            if (source.DynamicRange)
            {
                if (source.TargetKind == GraphAccessTargetKind.Buffer)
                {
                    if (!_frame.BufferRangeOverrides.TryGetValue(identity, out bufferRange))
                        throw new InvalidOperationException("A dynamic Buffer access range was not supplied.");
                }
                else if (!_frame.TextureRangeOverrides.TryGetValue(identity, out textureRange))
                {
                    throw new InvalidOperationException("A dynamic Texture access range was not supplied.");
                }
            }
            _accesses[i] = new FrameResourceAccess
            {
                Identity = identity,
                Pass = source.Pass,
                TargetKind = source.TargetKind,
                Target = source.Target,
                Mode = source.Mode,
                Coverage = source.Coverage,
                Sync = source.Sync,
                Access = source.Access,
                BufferRange = bufferRange,
                TextureRange = textureRange,
                TextureLayout = source.TextureLayout,
                ResultContents = source.ResultContents,
            };
            _accessIndices.Add(identity, i);
        }
        for (int i = 0; i < _frame.DynamicAccesses.Count; i++)
        {
            FrameResourceAccess source = _frame.DynamicAccesses[i];
            int destination = staticAccessCount + i;
            _accesses[destination] = source;
            _accessIndices.Add(source.Identity, destination);
        }

        PrepareAdjacency(ref _passAccesses, _passes.Length);
        for (int i = 0; i < _accesses.Length; i++)
        {
            FrameResourceAccess access = _accesses[i];
            access.PassIndex = ResolvePass(access.Pass);
            access.ResourceIndex = access.TargetKind switch
            {
                GraphAccessTargetKind.Buffer => ResolveBuffer(access.Target),
                GraphAccessTargetKind.Texture => ResolveTexture(access.Target),
                GraphAccessTargetKind.QueryPool => ResolveQueryPool(access.Target),
                GraphAccessTargetKind.RayTracingShaderTable => ResolveShaderTable(access.Target),
                _ => throw new ArgumentOutOfRangeException(nameof(access.TargetKind)),
            };
            if (access.TargetKind == GraphAccessTargetKind.Buffer)
                access.BufferRange = GraphStructureIndex.ResolveRange(
                    access.BufferRange,
                    _buffers[access.ResourceIndex].Description.Size);
            else if (access.TargetKind == GraphAccessTargetKind.Texture)
                ValidateTextureRange(_textures[access.ResourceIndex], access.TextureRange);
            else if (access.TargetKind == GraphAccessTargetKind.QueryPool)
                ValidateQueryRange(_queryPools[access.ResourceIndex], access.QueryRange);
            _accesses[i] = access;
            if (i >= staticAccessCount)
                _passAccesses[access.PassIndex].Add(i);
        }
        for (int staticPass = 0; staticPass < staticPassCount; staticPass++)
        {
            int framePass = ResolvePass(structureIndex.PassIds[staticPass]);
            foreach (int access in structureIndex.PassAccessIndices[staticPass])
                _passAccesses[framePass].Add(access);
        }

        PrepareAdjacency(ref _valuePredecessors, _passes.Length);
        PrepareAdjacency(ref _predecessors, _passes.Length);
        PrepareAdjacency(ref _successors, _passes.Length);
        PrepareArray(ref _live, _passes.Length);
        Array.Clear(_live);
    }

    private static void PrepareArray<T>(ref T[] values, int count)
    {
        if (values.Length == count) return;
        values = count == 0 ? Array.Empty<T>() : new T[count];
    }

    private static void PrepareAdjacency(ref List<int>[] values, int count)
    {
        if (values.Length != count)
        {
            values = count == 0 ? Array.Empty<List<int>>() : new List<int>[count];
            for (int i = 0; i < values.Length; i++) values[i] = [];
            return;
        }
        for (int i = 0; i < values.Length; i++) values[i].Clear();
    }

    private int ResolveBuffer(in GraphIdentity identity)
    {
        if (!_bufferIndices.TryGetValue(identity, out int value))
            throw new ArgumentException("The Buffer identity is invalid or stale.");
        return value;
    }

    private int ResolveTexture(in GraphIdentity identity)
    {
        if (!_textureIndices.TryGetValue(identity, out int value))
            throw new ArgumentException("The Texture identity is invalid or stale.");
        return value;
    }

    private int ResolveQueryPool(in GraphIdentity identity)
    {
        if (!_queryPoolIndices.TryGetValue(identity, out int value))
            throw new ArgumentException("The QueryPool identity is invalid or stale.");
        return value;
    }

    private int ResolveShaderTable(in GraphIdentity identity)
    {
        if (!_shaderTableIndices.TryGetValue(identity, out int value))
            throw new ArgumentException("The RayTracingShaderTable identity is invalid or stale.");
        return value;
    }

    private int ResolvePass(in GraphIdentity identity)
    {
        if (!_passIndices.TryGetValue(identity, out int value))
            throw new ArgumentException("The Pass identity is invalid or stale.");
        return value;
    }

    private static void ValidateQueryRange(in FrameQueryPool pool, in QueryRange range)
    {
        uint count = pool.Resource.Description.Count;
        if (range.QueryCount == 0 || range.FirstQuery >= count ||
            range.QueryCount > count - range.FirstQuery)
            throw new ArgumentOutOfRangeException(nameof(range));
    }

    private static void ValidateTextureRange(in FrameTexture texture, in TextureSubresourceRange range)
    {
        if (range.MipLevelCount == 0 || range.ArrayLayerCount == 0 ||
            range.FirstMipLevel >= texture.MipLevelCount ||
            range.MipLevelCount > texture.MipLevelCount - range.FirstMipLevel ||
            range.FirstArrayLayer >= texture.ArrayLayerCount ||
            range.ArrayLayerCount > texture.ArrayLayerCount - range.FirstArrayLayer ||
            range.Aspects == TextureAspects.None)
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }
    }
}

