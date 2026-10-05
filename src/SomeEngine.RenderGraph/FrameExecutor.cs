using System.Diagnostics;

namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly RenderGraphFrameState _frame;
    private readonly bool _shadingRateImages;
    private List<FrameBuffer> _buffers = [];
    private List<FrameTexture> _textures = [];
    private List<FrameQueryPool> _queryPools = [];
    private List<FrameRayTracingShaderTable> _shaderTables = [];
    private List<FrameView> _views = [];
    private List<FramePass> _passes = [];
    private List<FrameResourceAccess> _accesses = [];
    private readonly Dictionary<GraphIdentity, int> _bufferIndices = [];
    private readonly Dictionary<GraphIdentity, int> _textureIndices = [];
    private readonly Dictionary<GraphIdentity, int> _queryPoolIndices = [];
    private readonly Dictionary<GraphIdentity, int> _shaderTableIndices = [];
    private readonly Dictionary<GraphIdentity, int> _viewIndices = [];
    private readonly Dictionary<GraphIdentity, int> _passIndices = [];
    private readonly Dictionary<GraphIdentity, int> _accessIndices = [];
    private readonly List<int> _resourceAccessScratch = [];
    private readonly Stack<int> _livenessStack = new();
    private readonly List<int> _shaderReaderScratch = [];
    private ulong[] _bufferCoordinateScratch = [];
    private uint[] _queryCoordinateScratch = [];
    private int[] _writerScratch = [];
    private ResourceContentState[] _contentsScratch = [];
    private BufferContentScope[] _bufferContentScopeScratch = [];
    private List<int>[] _readerScratch = [];
    private List<int>[] _atomicWriterScratch = [];
    private TextureLayout?[] _layoutScratch = [];
    private int[] _criticalRanks = [];
    private int[] _scheduleIndegrees = [];
    private int[] _criticalTopological = [];
    private readonly List<int> _scheduleReady = [];
    private readonly PriorityQueue<int, long> _criticalReady = new();
    private readonly Dictionary<Queue, int> _queueTime =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<Queue> _queueCandidateScratch = [];
    private List<int>[] _valuePredecessors = [];
    private List<int>[] _predecessors = [];
    private List<int>[] _successors = [];
    private List<int>[] _passAccesses = [];
    private bool[] _live = [];
    private int[] _sameQueuePredecessor = [];
    private int[][] _startFrontiers = [];
    private int[][] _endFrontiers = [];
    private readonly Dictionary<Queue, int> _queueLanes = new(ReferenceEqualityComparer.Instance);
    private int _queueLaneCount;
    private FrameSubmissionMode _submissionMode;
    private RenderGraphDebugOptions _executionDebug;
    private bool _recordingChanged = true;
    private bool _reuseRecording;
    private int _waveCount;

    internal FrameExecutor(RenderGraphFrameState frame)
    {
        _frame = frame;
        _shadingRateImages = frame.Backend.TryGetCapability(frame.Graph.Device, out VariableRateShading? shading) &&
            shading is { ShadingRateImage: true };
    }

    internal void ApplyEdit(GraphEditChanges changes)
    {
        if (!changes.IsEmpty && !changes.LabelsOnly)
        {
            _analysisChanged = true;
            _bufferInputLocations.Clear();
        }
        _rowChanges.Buffers.UnionWith(changes.Buffers);
        _rowChanges.Textures.UnionWith(changes.Textures);
        _rowChanges.QueryPools.UnionWith(changes.QueryPools);
        _rowChanges.ShaderTables.UnionWith(changes.ShaderTables);
        _rowChanges.Views.UnionWith(changes.Views);
        _rowChanges.Passes.UnionWith(changes.Passes);
        foreach (GraphIdentity pass in changes.Passes) ChangeCommandData(pass);
        _rowChanges.Accesses.UnionWith(changes.Accesses);

        _changedOrderIdentities.UnionWith(changes.OrderPasses);
        foreach (GraphIdentity id in changes.Labels)
        {
            if (!_passIndices.TryGetValue(id, out int index) || !_frame.StructureIndex.Structure.Passes.Contains(id)) continue;
            GraphPass source = _frame.StructureIndex.Structure.Passes.Get(id);
            CollectionsMarshal.AsSpan(_passes)[index].Definition = source;
            CollectionsMarshal.AsSpan(_passes)[index].Label = source.Label;
        }
    }

    internal void Reset()
    {
        if ((_executionDebug & RenderGraphDebugOptions.DeclarationOrderScheduling) !=
            (_frame.Options.Debug & RenderGraphDebugOptions.DeclarationOrderScheduling))
        {
            _scheduleInitialized = false;
            for (int pass = 0; pass < _passes.Count; pass++) _scheduleSeeds.Add(pass);
        }
        if (_rowsReady && (_submissionMode != _frame.Options.SubmissionMode ||
            _executionDebug != _frame.Options.Debug))
        {
            _analysisChanged = true;
            _recordingChanged = true;
            for (int pass = 0; pass < _passes.Count; pass++)
            {
                _changedAnalysisPasses.Add(pass);
                ChangeRecording(pass);
            }
            foreach (FrameBuffer buffer in _buffers)
                if (buffer.Definition is not null && buffer.Lifetime == RenderGraphResourceLifetime.PerFrame)
                    ChangeBuffer(buffer.Identity);
            foreach (FrameTexture texture in _textures)
                if (texture.Definition is not null && texture.Lifetime == RenderGraphResourceLifetime.PerFrame)
                    ChangeTexture(texture.Identity);
            foreach (FrameView view in _views)
                if (view.Definition is not null) ChangeView(view.Identity);
        }

        _submissionMode = _frame.Options.SubmissionMode;
        _executionDebug = _frame.Options.Debug;
        _reuseAnalysis = false;
        _submittedCompletions.Clear();
    }

    internal int Execute(Span<QueueCompletion> destination)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Execute);
        BuildRows();
        ConnectParameterBindings();
        ObserveChangedBoundaries();
        _reuseAnalysis = TryReuseAnalysis();
        if (!_reuseAnalysis)
        {
            UpdateDependencies();
            ResolveSchedule();
            ResolveFrontiersAndLifetimes();
            SaveAnalysis();
        }
        Materialize();
        RefreshParameterBindings();
        ResolveSynchronization();
        PublishDiagnostics();
        return RecordAndSubmit(destination);
    }

    private void BuildRows()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Rows);
        BeginRows();
        GraphStructureIndex structureIndex = _frame.StructureIndex;
        UpdateBufferRanges();
        int staticBufferCount = structureIndex.Structure.Buffers.Count;
        int staticTextureCount = structureIndex.Structure.Textures.Count;
        int staticQueryPoolCount = structureIndex.Structure.QueryPools.Count;
        int staticShaderTableCount = structureIndex.Structure.ShaderTables.Count;
        int staticViewCount = structureIndex.Structure.Views.Count;

        PrepareRows(_buffers, staticBufferCount + _frame.DynamicBuffers.Count);
        foreach (int i in ChangedRows(structureIndex.Structure.Buffers, _bufferIndices, _rowChanges.Buffers))
        {
            GraphBuffer source = structureIndex.Structure.Buffers.Rows[i];
            GraphIdentity identity = structureIndex.BufferIds[i];
            Buffer? resource = source.PersistentResource ?? source.RegisteredResource ?? source.BoundResource;
            BufferBoundaryState[]? boundaryStates = source.BoundaryStates;
            if (_frame.TryGetBufferData(identity, out var data))
            {
                resource = data.Buffer;
                boundaryStates = data.BoundaryStates;
            }
            DevelopmentCheck.Require(source.Ownership != RenderGraphResourceOwnership.CallerOwned || resource is not null,
                $"External Buffer '{source.Label}' is not bound.");
            SetBufferRow(i, new FrameBuffer
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
                UploadData = data,
                FirstPass = -1,
                LastPass = -1,
            });
            _bufferIndices[identity] = i;
        }
        for (int i = 0; i < _frame.DynamicBuffers.Count; i++)
        {
            FrameBuffer source = _frame.DynamicBuffers[i];
            int destination = staticBufferCount + i;
            SetBufferRow(destination, source);
        }

        PrepareRows(_textures, staticTextureCount + _frame.DynamicTextures.Count);
        foreach (int i in ChangedRows(structureIndex.Structure.Textures, _textureIndices, _rowChanges.Textures))
        {
            GraphTexture source = structureIndex.Structure.Textures.Rows[i];
            GraphIdentity identity = structureIndex.TextureIds[i];
            Texture? resource = source.PersistentResource ?? source.RegisteredResource ?? source.BoundResource;
            TextureBoundaryState[]? boundaryStates = source.BoundaryStates;
            DevelopmentCheck.Require(source.Ownership != RenderGraphResourceOwnership.CallerOwned || resource is not null,
                $"External Texture '{source.Label}' is not bound.");
            SetTextureRow(i, new FrameTexture
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
                FirstPass = -1,
                LastPass = -1,
            });
            _textureIndices[identity] = i;
        }
        for (int i = 0; i < _frame.DynamicTextures.Count; i++)
        {
            FrameTexture source = _frame.DynamicTextures[i];
            int destination = staticTextureCount + i;
            SetTextureRow(destination, source);
        }

        PrepareRows(_queryPools, staticQueryPoolCount);
        foreach (int i in ChangedRows(structureIndex.Structure.QueryPools, _queryPoolIndices, _rowChanges.QueryPools))
        {
            GraphQueryPool source = structureIndex.Structure.QueryPools.Rows[i];
            GraphIdentity identity = structureIndex.QueryPoolIds[i];
            _rebuiltRowCount++;
            _queryPools[i] = new FrameQueryPool
            {
                Identity = identity,
                Definition = source,
                Resource = source.Resource,
                EntryBoundaryStates = source.BoundaryStates,
                Contents = _queryPools[i].Contents,
            };
            _queryPoolIndices[identity] = i;
            ObserveQueryContents(i);
            ChangeAnalysisResource(GraphAccessTargetKind.QueryPool, i);
        }

        PrepareRows(_shaderTables, staticShaderTableCount);
        foreach (int i in ChangedRows(structureIndex.Structure.ShaderTables, _shaderTableIndices, _rowChanges.ShaderTables))
        {
            GraphRayTracingShaderTable source = structureIndex.Structure.ShaderTables.Rows[i];
            GraphIdentity identity = structureIndex.ShaderTableIds[i];
            _rebuiltRowCount++;
            _shaderTables[i] = new FrameRayTracingShaderTable
            {
                Identity = identity,
                Definition = source,
                Resource = source.Resource,
                Inventory = source.Inventory,
                EntryBoundaryStates = source.BoundaryStates,
                Contents = _shaderTables[i].Contents,
            };
            _shaderTableIndices[identity] = i;
            ObserveShaderTableContents(i);
            ChangeAnalysisResource(GraphAccessTargetKind.RayTracingShaderTable, i);
        }

        UpdateInputRows();
        PrepareRows(_views, staticViewCount + _frame.DynamicViews.Count);
        foreach (int i in ChangedRows(structureIndex.Structure.Views, _viewIndices, _rowChanges.Views))
        {
            GraphView source = structureIndex.Structure.Views.Rows[i];
            GraphIdentity identity = structureIndex.ViewIds[i];
            GraphIdentity bufferIdentity = source.Buffer;
            GraphIdentity textureIdentity = source.Texture;
            TextureSubresourceRange viewTextureRange = source.TextureRange;
            BufferRange bufferRange = source.BufferRange;
            bool bufferRangeOverridden = false;
            if (source.Kind is GraphViewKind.BufferCbv or
                GraphViewKind.BufferSrv or
                GraphViewKind.BufferUav)
            {
                BufferRange? current = ViewBufferRangeOverride(source);
                bufferRangeOverridden = current.HasValue;
                bufferRange = current ?? source.BufferRange;
            }
            if (source.BufferInput is not null)
            {
                GraphBufferSlice value = _frame.ResolveBufferInput(source.BufferInput);
                bufferIdentity = value.Buffer.Value;
                bufferRange = value.ResolveRange(bufferRange);
                bufferRangeOverridden = true;
            }
            if (source.TextureInput is not null)
            {
                GraphTextureId texture = _frame.ResolveTextureInput(source.TextureInput);
                textureIdentity = texture.Value;
                viewTextureRange = _frame.ResolveTextureRange(texture, source.TextureRange.MipLevelCount == 0 ? null : source.TextureRange);
            }
            SetViewRow(i, new FrameView
            {
                Identity = identity,
                Definition = source,
                Kind = source.Kind,
                Buffer = bufferIdentity,
                Texture = textureIdentity,
                AdditionalBuffer = source.AdditionalBuffer,
                BufferRange = bufferRange,
                TextureRange = viewTextureRange,
                BufferFormat = source.BufferFormat,
                TextureFormat = source.TextureFormat,
                StructureStride = source.StructureStride,
                CounterOffset = source.CounterOffset,
                Dimension = source.Dimension,
                ReadOnlyDepth = source.ReadOnlyDepth,
                ReadOnlyStencil = source.ReadOnlyStencil,
                Label = source.Label,
                View = bufferRangeOverridden ? null : source.PersistentView,
            });
            _viewIndices[identity] = i;
        }
        for (int i = 0; i < _frame.DynamicViews.Count; i++)
        {
            FrameView source = _frame.DynamicViews[i];
            int destination = staticViewCount + i;
            SetViewRow(destination, source);
        }

        PreparePassRows();
        EnsureCapacity(ref _passAccesses, _passes.Count);
        foreach (int sourceRow in CollectChangedRows(structureIndex.Structure.Passes, _rowChanges.Passes))
        {
            GraphIdentity identity = structureIndex.PassIds[sourceRow];
            GraphPass source = structureIndex.Structure.Passes.Get(identity);
            int i = _passIndices[identity];
            SetPassRow(i, new FramePass
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
                DeclarationOrdinal = source.DeclarationOrdinal,
                Enabled = source.Enabled && structureIndex.Structure.IsRegionEnabled(source.Region) &&
                    _frame.HasBufferUploads(source.CallbackStorage),
                CommandSource = source.CommandSource,
                PersistentCallbacks = source.CallbackStorage,
                Region = source.Region,
            });
        }

        for (int i = 0; i < _frame.DynamicPasses.Count; i++)
        {
            FramePass source = _frame.DynamicPasses[i];
            int ordinal = _dynamicPassPositions[i].Ordinal;
            GraphRegionEntry interval = structureIndex.Structure.GetRegionInterval(source.Region);
            long insertion = source.Region.IsValid ? interval.Start :
                interval.End;
            source.DeclarationOrdinal = checked(insertion + ordinal + 1);
            if (source.IsBufferUpload) source.DeclarationOrdinal = long.MinValue + i;
            DevelopmentCheck.Require(!source.Region.IsValid || source.DeclarationOrdinal < interval.End,
                "Frame passes must fit inside their region's declaration interval.");
            source.Enabled &= structureIndex.Structure.IsRegionEnabled(source.Region);
            SetPassRow(_dynamicPassRows[i], source);
        }

        int previousRangeStart = _rangeReadStart;
        PrepareAccessRows();
        bool rangeReadsChanged = PrepareBufferRangeReads(
            _accessRowCount, out int accessCount);
        if (rangeReadsChanged)
            for (int index = previousRangeStart; index < _accesses.Count; index++) DetachAccess(index);
        PrepareRows(_accesses, accessCount);
        foreach (int sourceRow in CollectChangedRows(structureIndex.Structure.Accesses, _rowChanges.Accesses))
        {
            GraphIdentity identity = structureIndex.AccessIds[sourceRow];
            PassResourceAccess source = structureIndex.Structure.Accesses.Get(identity);
            int i = _accessIndices[identity];
            BufferRange bufferRange = source.BufferRange;
            TextureSubresourceRange textureRange = source.TextureRange;
            GraphIdentity target = source.Target;
            if (source.BufferInput is not null)
            {
                GraphBufferSlice value = _frame.ResolveBufferInput(source.BufferInput);
                target = value.Buffer.Value;
                bufferRange = value.ResolveRange(source.BufferRange);
            }
            if (source.TextureInput is not null)
            {
                GraphTextureId texture = _frame.ResolveTextureInput(source.TextureInput);
                target = texture.Value;
                textureRange = _frame.ResolveTextureRange(texture, source.TextureRange.MipLevelCount == 0 ? null : source.TextureRange);
            }
            if (source.ParameterInput is not null)
            {
                ParameterInputValue value = _frame.ResolveParameterInput(source.ParameterInput);
                target = source.ParameterSecondary ? value.Secondary : value.Resource;
                if (!target.IsValid)
                {
                    DetachAccess(i);
                    _accesses[i] = default;
                    continue;
                }
                bufferRange = source.ParameterSecondary ? value.SecondaryBufferRange : value.BufferRange;
                textureRange = source.ParameterSecondary ? value.SecondaryTextureRange : value.TextureRange;
            }
            if (source.DynamicRange)
            {
                if (source.TargetKind == GraphAccessTargetKind.Buffer)
                {
                    DevelopmentCheck.Require(source.CurrentBufferRange.HasValue, "A dynamic Buffer access range was not supplied.");
                    bufferRange = source.CurrentBufferRange.GetValueOrDefault();
                }
                else
                {
                    DevelopmentCheck.Require(source.CurrentTextureRange.HasValue, "A dynamic Texture access range was not supplied.");
                    textureRange = source.CurrentTextureRange.GetValueOrDefault();
                }
            }
            SetAccessRow(i, new FrameResourceAccess
            {
                Identity = identity,
                Pass = source.Pass,
                TargetKind = source.TargetKind,
                Target = target,
                Mode = source.Mode,
                Coverage = source.Coverage,
                Sync = source.Sync,
                Access = source.Access,
                BufferRange = bufferRange,
                BufferDescriptor = source.BufferDescriptor,
                BufferDescriptorOnly = source.BufferDescriptorOnly,
                TextureRange = textureRange,
                QueryRange = source.QueryRange,
                TextureLayout = source.TextureLayout,
                ResultContents = source.ResultContents,
                BufferContentScope = source.BufferContentScope,
                UnorderedAccessHazards = source.UnorderedAccessHazards,
            });
        }
        for (int i = 0; i < _frame.DynamicAccesses.Count; i++)
        {
            FrameResourceAccess source = _frame.DynamicAccesses[i];
            int destination = _dynamicAccessRows[i];
            SetAccessRow(destination, source);
        }

        BuildBufferRangeReads(rangeReadsChanged);

        EndRows();
    }

    private static void PrepareRows<T>(List<T> values, int count)
    {
        if (values.Count != count) CollectionsMarshal.SetCount(values, count);
    }

    private static void PrepareArray<T>(ref T[] values, int count)
    {
        if (values.Length == count) return;
        values = count == 0 ? Array.Empty<T>() : new T[count];
    }

    private int ResolveBuffer(in GraphIdentity identity)
    {
        bool found = TryResolveBuffer(identity, out int value);
        DevelopmentCheck.Require(found, "The Buffer identity is invalid or stale.");
        return value;
    }

    private int ResolveTexture(in GraphIdentity identity)
    {
        bool found = TryResolveTexture(identity, out int value);
        DevelopmentCheck.Require(found, "The Texture identity is invalid or stale.");
        return value;
    }

    private int ResolveQueryPool(in GraphIdentity identity)
    {
        bool found = _queryPoolIndices.TryGetValue(identity, out int value);
        DevelopmentCheck.Require(found, "The QueryPool identity is invalid or stale.");
        return value;
    }

    private int ResolveShaderTable(in GraphIdentity identity)
    {
        bool found = _shaderTableIndices.TryGetValue(identity, out int value);
        DevelopmentCheck.Require(found, "The RayTracingShaderTable identity is invalid or stale.");
        return value;
    }

    private int ResolvePass(in GraphIdentity identity)
    {
        bool found = TryResolvePass(identity, out int value);
        DevelopmentCheck.Require(found, "The Pass identity is invalid or stale.");
        return value;
    }

    private bool TryResolveBuffer(in GraphIdentity identity, out int index) =>
        identity.Owner == _frame.Graph.Identity
            ? _bufferIndices.TryGetValue(identity, out index)
            : TryResolveDynamicRow(identity, GraphIdentityDomain.Buffer,
                _frame.StructureIndex.Structure.Buffers.Count, _frame.DynamicBuffers.Count, out index);

    private bool TryResolveTexture(in GraphIdentity identity, out int index) =>
        identity.Owner == _frame.Graph.Identity
            ? _textureIndices.TryGetValue(identity, out index)
            : TryResolveDynamicRow(identity, GraphIdentityDomain.Texture,
                _frame.StructureIndex.Structure.Textures.Count, _frame.DynamicTextures.Count, out index);

    private bool TryResolvePass(in GraphIdentity identity, out int index) =>
        identity.Owner == _frame.Graph.Identity
            ? _passIndices.TryGetValue(identity, out index)
            : TryResolveFramePass(identity, out index);

    private bool TryResolveFramePass(in GraphIdentity identity, out int index)
    {
        index = -1;
        if (identity.Owner != _frame.Identity || identity.Domain != GraphIdentityDomain.Pass ||
            identity.Generation != 1 || (uint)identity.Slot >= (uint)_frame.DynamicPasses.Count) return false;
        index = _dynamicPassRows[identity.Slot];
        return true;
    }

    private bool TryResolveView(in GraphIdentity identity, out int index) =>
        identity.Owner == _frame.Graph.Identity
            ? _viewIndices.TryGetValue(identity, out index)
            : TryResolveDynamicRow(identity, GraphIdentityDomain.View,
                _frame.StructureIndex.Structure.Views.Count, _frame.DynamicViews.Count, out index);

    private bool TryResolveDynamicRow(in GraphIdentity identity, GraphIdentityDomain domain,
        int first, int count, out int index)
    {
        index = first + identity.Slot;
        return identity.Owner == _frame.Identity && identity.Domain == domain &&
            identity.Generation == 1 && (uint)identity.Slot < (uint)count;
    }

    [Conditional("DEBUG")]
    private static void ValidateQueryRange(in FrameQueryPool pool, in QueryRange range)
    {
        uint count = pool.Resource.Description.Count;
        if (range.QueryCount == 0 || range.FirstQuery >= count ||
            range.QueryCount > count - range.FirstQuery)
            DevelopmentCheck.Fail("The Query range is outside the declared QueryPool.");
    }

    [Conditional("DEBUG")]
    private static void ValidateTextureRange(in FrameTexture texture, in TextureSubresourceRange range)
    {
        if (range.MipLevelCount == 0 || range.ArrayLayerCount == 0 ||
            range.FirstMipLevel >= texture.MipLevelCount ||
            range.MipLevelCount > texture.MipLevelCount - range.FirstMipLevel ||
            range.FirstArrayLayer >= texture.ArrayLayerCount ||
            range.ArrayLayerCount > texture.ArrayLayerCount - range.FirstArrayLayer ||
            range.Aspects == TextureAspects.None)
        {
            DevelopmentCheck.Fail("The Texture range is outside the declared Texture.");
        }
    }

    private readonly GraphEditChanges _rowChanges = new();
    private readonly HashSet<int> _rowIndexSet = [];
    private readonly List<int> _rowIndexScratch = [];
    private bool _rowsReady;
    private int _rebuiltRowCount;
    private int _staticBufferCount, _staticTextureCount;

    internal void ChangeBuffer(GraphIdentity identity, bool refreshViews = true)
    {
        _rowChanges.Buffers.Add(identity);
        if (!refreshViews) return;
        foreach (GraphIdentity view in _frame.StructureIndex.ResourceViews.GetValueOrDefault(identity, []))
            _rowChanges.Views.Add(view);
    }

    internal void ChangeTexture(GraphIdentity identity, bool refreshViews = true)
    {
        _rowChanges.Textures.Add(identity);
        if (!refreshViews) return;
        foreach (GraphIdentity view in _frame.StructureIndex.ResourceViews.GetValueOrDefault(identity, []))
            _rowChanges.Views.Add(view);
    }

    internal void ChangeView(GraphIdentity identity) => _rowChanges.Views.Add(identity);
    internal void ChangePass(GraphIdentity identity)
    {
        _rowChanges.Passes.Add(identity);
        ChangeCommandData(identity);
    }
    internal void ChangeAccess(GraphIdentity identity) => _rowChanges.Accesses.Add(identity);

    private List<int> ChangedRows<T>(GraphRows<T> source, Dictionary<GraphIdentity, int> indices, HashSet<GraphIdentity> changes)
    {
        _rowIndexSet.Clear();
        _rowIndexScratch.Clear();
        if (!_rowsReady)
        {
            for (int index = 0; index < source.Count; index++) _rowIndexScratch.Add(index);
        }
        else
        {
            foreach (GraphIdentity identity in changes)
            {
                if (indices.TryGetValue(identity, out int previous) && previous < source.Count) _rowIndexSet.Add(previous);
                if (source.Contains(identity)) _rowIndexSet.Add(source.DenseIndex(identity));
                else indices.Remove(identity);
            }
            _rowIndexScratch.AddRange(_rowIndexSet);
        }
        foreach (int index in _rowIndexScratch) indices[source.Identities[index]] = index;
        return _rowIndexScratch;
    }

    private void BeginRows()
    {
        _rebuiltRowCount = 0;
        _analyzedPasses.Clear();
        FrameTransientResourceAllocator memory = _frame.Slot.TransientResources!;
        GraphStructure structure = _frame.StructureIndex.Structure;
        _analysisChanged |= _buffers.Count != structure.Buffers.Count + _frame.DynamicBuffers.Count ||
            _textures.Count != structure.Textures.Count + _frame.DynamicTextures.Count ||
            _queryPools.Count != structure.QueryPools.Count || _shaderTables.Count != structure.ShaderTables.Count;

        int retainedBuffers = _staticBufferCount == structure.Buffers.Count ? _frame.DynamicBuffers.Count : 0;
        int retainedTextures = _staticTextureCount == structure.Textures.Count ? _frame.DynamicTextures.Count : 0;
        for (int index = _staticBufferCount + retainedBuffers; index < _buffers.Count; index++)
        {
            memory.RemoveResource(GraphAccessTargetKind.Buffer, _buffers[index].AllocationIdentity);
            _allocationRows.Remove(_buffers[index].AllocationIdentity);
            if (_buffers[index].Resource is { } resource) _resourceRows.Remove(resource);
        }
        for (int index = _staticTextureCount + retainedTextures; index < _textures.Count; index++)
        {
            memory.RemoveResource(GraphAccessTargetKind.Texture, _textures[index].AllocationIdentity);
            _allocationRows.Remove(_textures[index].AllocationIdentity);
            if (_textures[index].Resource is { } resource) _resourceRows.Remove(resource);
        }
        for (int index = structure.Buffers.Count + _frame.DynamicBuffers.Count; index < _buffers.Count; index++)
            ReconnectResourceParameters((GraphAccessTargetKind.Buffer, index));
        for (int index = structure.Textures.Count + _frame.DynamicTextures.Count; index < _textures.Count; index++)
            ReconnectResourceParameters((GraphAccessTargetKind.Texture, index));
        for (int index = structure.Views.Count + _frame.DynamicViews.Count; index < _views.Count; index++)
        {
            ReconnectViewParameters(index);
            DisconnectViewResources(index);
        }

        foreach (GraphIdentity id in _rowChanges.Buffers)
            if (!structure.Buffers.Contains(id))
            {
                memory.RemoveResource(GraphAccessTargetKind.Buffer, id);
                _allocationRows.Remove(id);
                if (_bufferIndices.TryGetValue(id, out int index) && _buffers[index].Resource is { } resource)
                    _resourceRows.Remove(resource);
            }
        foreach (GraphIdentity id in _rowChanges.Textures)
            if (!structure.Textures.Contains(id))
            {
                memory.RemoveResource(GraphAccessTargetKind.Texture, id);
                _allocationRows.Remove(id);
                if (_textureIndices.TryGetValue(id, out int index) && _textures[index].Resource is { } resource)
                    _resourceRows.Remove(resource);
            }
    }

    private void EndRows()
    {
        PrepareDynamicOrders();

        GraphStructure structure = _frame.StructureIndex.Structure;
        _staticBufferCount = structure.Buffers.Count;
        _staticTextureCount = structure.Textures.Count;
        _rowChanges.Buffers.Clear();
        _rowChanges.Textures.Clear();
        _rowChanges.QueryPools.Clear();
        _rowChanges.ShaderTables.Clear();
        _rowChanges.Views.Clear();
        _rowChanges.Passes.Clear();
        _rowChanges.Accesses.Clear();
        _rowsReady = true;
    }

    private void SetBufferRow(int index, FrameBuffer row)
    {
        _rebuiltRowCount++;
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.RowsSetBufferRow);
        FrameBuffer previous = _buffers[index];
        if (previous.Identity != row.Identity && (previous.Definition is not null || row.Definition is not null))
            ReconnectResourceParameters((GraphAccessTargetKind.Buffer, index));
        bool retained = previous.AllocationIdentity.IsValid && previous.Description == row.Description &&
            previous.MemoryType == row.MemoryType && previous.Ownership == row.Ownership && previous.Lifetime == row.Lifetime &&
            (previous.Identity == row.Identity || previous.Definition is null && row.Definition is null);
        if (retained && row.Resource is null && previous.Resource is not null &&
            _frame.Slot.TransientResources!.IsCached(previous.Resource))
        {
            row.Resource = previous.Resource;
            row.Placement = previous.Placement;
            row.Requirements = previous.Requirements;
            row.EntryBoundaryStates = _frame.Slot.TransientResources!.FindCachedBufferStates(
                previous.AllocationIdentity, previous.Resource);


        }
        if (!retained && previous.AllocationIdentity.IsValid)
            _frame.Slot.TransientResources!.RemoveResource(GraphAccessTargetKind.Buffer, previous.AllocationIdentity);
        row.AllocationIdentity = retained ? previous.AllocationIdentity : row.Identity;
        row.FirstPass = previous.FirstPass;
        row.LastPass = previous.LastPass;
        ObserveBuffer(index, ref row);
        row.Synchronization = previous.Synchronization;
        ObserveBufferSynchronization(index, row);
        if (previous.AllocationIdentity != row.AllocationIdentity) _allocationRows.Remove(previous.AllocationIdentity);
        _allocationRows[row.AllocationIdentity] = (GraphAccessTargetKind.Buffer, index);
        _buffers[index] = row;
        if (previous.Resource is not null && !ReferenceEquals(previous.Resource, row.Resource)) _resourceRows.Remove(previous.Resource);
        if (row.Resource is not null) _resourceRows[row.Resource] = (GraphAccessTargetKind.Buffer, index);
        if (!retained || !ReferenceEquals(previous.Resource, row.Resource))
            MaterializationChanged((GraphAccessTargetKind.Buffer, index), updateViewDescriptions: !retained);
        if (!retained)
            foreach (GraphIdentity access in _frame.StructureIndex.ResourceAccesses.GetValueOrDefault(row.Identity, []))
                _rowChanges.Accesses.Add(access);
    }

    private void SetTextureRow(int index, FrameTexture row)
    {
        _rebuiltRowCount++;
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.RowsSetTextureRow);
        FrameTexture previous = _textures[index];
        if (previous.Identity != row.Identity && (previous.Definition is not null || row.Definition is not null))
            ReconnectResourceParameters((GraphAccessTargetKind.Texture, index));
        bool retained = previous.AllocationIdentity.IsValid && previous.Dimension == row.Dimension &&
            previous.Width == row.Width && previous.Height == row.Height && previous.Depth == row.Depth &&
            previous.MipLevelCount == row.MipLevelCount && previous.ArrayLayerCount == row.ArrayLayerCount &&
            previous.SampleCount == row.SampleCount && previous.Format == row.Format && previous.Usages == row.Usages &&
            previous.Ownership == row.Ownership && previous.Lifetime == row.Lifetime && previous.NodePlacement == row.NodePlacement &&
            previous.PermittedViewFormats.AsSpan().SequenceEqual(row.PermittedViewFormats) &&
            (previous.Identity == row.Identity || previous.Definition is null && row.Definition is null);
        if (retained && row.Resource is null && previous.Resource is not null &&
            _frame.Slot.TransientResources!.IsCached(previous.Resource))
        {
            row.Resource = previous.Resource;
            row.Placement = previous.Placement;
            row.Requirements = previous.Requirements;
            row.EntryBoundaryStates = _frame.Slot.TransientResources!.FindCachedTextureStates(
                previous.AllocationIdentity, previous.Resource);

        }
        if (!retained && previous.AllocationIdentity.IsValid)
            _frame.Slot.TransientResources!.RemoveResource(GraphAccessTargetKind.Texture, previous.AllocationIdentity);
        row.AllocationIdentity = retained ? previous.AllocationIdentity : row.Identity;
        row.FirstPass = previous.FirstPass;
        row.LastPass = previous.LastPass;
        ObserveTexture(index, ref row);
        row.Synchronization = previous.Synchronization;
        ObserveTextureSynchronization(index, row);
        if (previous.AllocationIdentity != row.AllocationIdentity) _allocationRows.Remove(previous.AllocationIdentity);
        _allocationRows[row.AllocationIdentity] = (GraphAccessTargetKind.Texture, index);
        _textures[index] = row;
        if (previous.Resource is not null && !ReferenceEquals(previous.Resource, row.Resource)) _resourceRows.Remove(previous.Resource);
        if (row.Resource is not null) _resourceRows[row.Resource] = (GraphAccessTargetKind.Texture, index);
        if (!retained || !ReferenceEquals(previous.Resource, row.Resource))
            MaterializationChanged((GraphAccessTargetKind.Texture, index), updateViewDescriptions: !retained);
        if (!retained)
            foreach (GraphIdentity access in _frame.StructureIndex.ResourceAccesses.GetValueOrDefault(row.Identity, []))
                _rowChanges.Accesses.Add(access);
    }

    private void SetViewRow(int index, FrameView row)
    {
        _rebuiltRowCount++;
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.RowsSetViewRow);
        ConnectViewResources(index, ref row);
        FrameView previous = _views[index];
        if (previous.Identity != row.Identity) ReconnectViewParameters(index);
        if (row.Kind != GraphViewKind.BufferCbv && row.View is null &&
            TryResolveViewResources(row, out Resource? primary, out Resource? secondary))
            row.View = _frame.Slot.TransientResources!.FindCachedView(row, primary!, secondary);
        _views[index] = row;
        if (!ReferenceEquals(previous.View, row.View) || previous.BufferIndex != row.BufferIndex ||
            previous.BufferRange != row.BufferRange)
        {
            ChangeViewParameters(index);
            MarkViewCommands(GraphAccessTargetKind.Buffer, row.BufferIndex);
            MarkViewCommands(GraphAccessTargetKind.Texture, row.TextureIndex);
            MarkViewCommands(GraphAccessTargetKind.Buffer, row.AdditionalBufferIndex);
        }
        if (row.View is null)
            _materializeViews.Add(index);
        if (!ReferenceEquals(previous.View, row.View) && row.Texture.IsValid &&
            TryResolveTexture(row.Texture, out int texture) &&
            _resourceAccessLists.TryGetValue((GraphAccessTargetKind.Texture, texture), out List<int>? accesses))
            foreach (int access in accesses) InvalidateRasterRendering(_accesses[access].PassIndex);
    }

    private readonly Dictionary<(GraphAccessTargetKind Kind, int Index), List<int>> _resourceAccessLists = [];

    private void SetAccessRow(int index, FrameResourceAccess access)
    {
        _rebuiltRowCount++;
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.RowsSetAccessRow);
        access.PassIndex = ResolvePass(access.Pass);
        DevelopmentCheck.Require(Enum.IsDefined(access.TargetKind), "The resource access target kind is invalid.");
        access.ResourceIndex = access.TargetKind switch
        {
            GraphAccessTargetKind.Buffer => ResolveBuffer(access.Target),
            GraphAccessTargetKind.Texture => ResolveTexture(access.Target),
            GraphAccessTargetKind.QueryPool => ResolveQueryPool(access.Target),
            _ => ResolveShaderTable(access.Target),
        };
        if (access.TargetKind == GraphAccessTargetKind.Buffer)
        {
            access.BufferRange = GraphStructureIndex.ResolveRange(access.BufferRange, _buffers[access.ResourceIndex].Description.Size);
            if (access.BufferDescriptor)
                access.SharedBufferRanges = _frame.BufferRanges.GetValueOrDefault(access.Target) ??
                    (access.BufferDescriptorOnly ? BufferRangeSet.Empty : null);
        }
        else if (access.TargetKind == GraphAccessTargetKind.Texture)
            ValidateTextureRange(_textures[access.ResourceIndex], access.TextureRange);
        else if (access.TargetKind == GraphAccessTargetKind.QueryPool)
            ValidateQueryRange(_queryPools[access.ResourceIndex], access.QueryRange);
        FrameResourceAccess previous = _accesses[index];
        if (previous.Connected && previous.PassIndex == access.PassIndex &&
            previous.TargetKind == access.TargetKind && previous.ResourceIndex == access.ResourceIndex)
        {
            access.PassAdjacencyIndex = previous.PassAdjacencyIndex;
            access.ResourceAdjacencyIndex = previous.ResourceAdjacencyIndex;
            access.Connected = true;
            ObserveAccess(index, access);
            _accesses[index] = access;
            return;
        }
        _stateConnectionChanges.Add((access.TargetKind, access.ResourceIndex));
        DetachAccess(index);
        List<int> passAccesses = _passAccesses[access.PassIndex];
        access.PassAdjacencyIndex = passAccesses.Count;
        passAccesses.Add(index);
        var key = (access.TargetKind, access.ResourceIndex);
        if (!_resourceAccessLists.TryGetValue(key, out List<int>? resourceAccesses))
        {
            resourceAccesses = [];
            _resourceAccessLists.Add(key, resourceAccesses);
        }
        access.ResourceAdjacencyIndex = resourceAccesses.Count;
        resourceAccesses.Add(index);
        access.Connected = true;
        ObserveAccess(index, access);
        _accesses[index] = access;
    }

    private void DetachAccess(int index)
    {
        FrameResourceAccess access = _accesses[index];
        if (!access.Connected) return;
        ChangeAnalysisResource(access.TargetKind, access.ResourceIndex);
        _stateConnectionChanges.Add((access.TargetKind, access.ResourceIndex));
        List<int> passAccesses = _passAccesses[access.PassIndex];
        int moved = passAccesses[^1];
        passAccesses[access.PassAdjacencyIndex] = moved;
        CollectionsMarshal.AsSpan(_accesses)[moved].PassAdjacencyIndex = access.PassAdjacencyIndex;
        passAccesses.RemoveAt(passAccesses.Count - 1);
        List<int> resourceAccesses = _resourceAccessLists[(access.TargetKind, access.ResourceIndex)];
        moved = resourceAccesses[^1];
        resourceAccesses[access.ResourceAdjacencyIndex] = moved;
        CollectionsMarshal.AsSpan(_accesses)[moved].ResourceAdjacencyIndex = access.ResourceAdjacencyIndex;
        resourceAccesses.RemoveAt(resourceAccesses.Count - 1);
        CollectionsMarshal.AsSpan(_accesses)[index].Connected = false;
    }

    private readonly Dictionary<(GraphAccessTargetKind Kind, int Index), HashSet<int>> _resourceViewRows = [];
    private void ConnectViewResources(int index, ref FrameView row)
    {
        int buffer = row.Buffer.IsValid ? ResolveBuffer(row.Buffer) : -1;
        int texture = row.Texture.IsValid ? ResolveTexture(row.Texture) : -1;
        int additional = row.AdditionalBuffer.IsValid ? ResolveBuffer(row.AdditionalBuffer) : -1;
        row.BufferIndex = buffer;
        row.TextureIndex = texture;
        row.AdditionalBufferIndex = additional;
        row.Connected = true;
        FrameView previous = _views[index];
        if (previous.Connected && previous.BufferIndex == buffer && previous.TextureIndex == texture &&
            previous.AdditionalBufferIndex == additional) return;
        DisconnectViewResources(index);
        Add(GraphAccessTargetKind.Buffer, buffer);
        Add(GraphAccessTargetKind.Texture, texture);
        Add(GraphAccessTargetKind.Buffer, additional);
        void Add(GraphAccessTargetKind kind, int resource)
        {
            if (resource < 0) return;
            if (!_resourceViewRows.TryGetValue((kind, resource), out HashSet<int>? views)) _resourceViewRows.Add((kind, resource), views = []);
            views.Add(index);
        }
    }

    private void DisconnectViewResources(int index)
    {
        FrameView previous = _views[index];
        if (!previous.Connected) return;
        Remove(GraphAccessTargetKind.Buffer, previous.BufferIndex);
        Remove(GraphAccessTargetKind.Texture, previous.TextureIndex);
        Remove(GraphAccessTargetKind.Buffer, previous.AdditionalBufferIndex);
        CollectionsMarshal.AsSpan(_views)[index].Connected = false;
        void Remove(GraphAccessTargetKind kind, int resource)
        {
            if (resource < 0 || !_resourceViewRows.TryGetValue((kind, resource), out HashSet<int>? views)) return;
            views.Remove(index);
            if (views.Count == 0) _resourceViewRows.Remove((kind, resource));
        }
    }

    private List<ResolvedBufferRead> _bufferReads = [];
    private readonly Dictionary<GraphIdentity, BufferRangeSet> _previousBufferRanges = [];

    private void UpdateBufferRanges()
    {
        foreach (GraphIdentity buffer in _previousBufferRanges.Keys)
            if (!_frame.BufferRanges.ContainsKey(buffer))
                ChangeDescriptorReads(buffer);
        foreach ((GraphIdentity buffer, BufferRangeSet ranges) in _frame.BufferRanges)
            if (!_previousBufferRanges.TryGetValue(buffer, out BufferRangeSet? previous) || !ReferenceEquals(ranges, previous))
                ChangeDescriptorReads(buffer);
        _previousBufferRanges.Clear();
        foreach ((GraphIdentity buffer, BufferRangeSet ranges) in _frame.BufferRanges)
            _previousBufferRanges.Add(buffer, ranges);
    }

    private void ChangeDescriptorReads(GraphIdentity buffer)
    {
        GraphStructureIndex index = _frame.StructureIndex;
        foreach (GraphIdentity access in index.ResourceAccesses.GetValueOrDefault(buffer, []))
            if (index.Structure.Accesses.Get(access).BufferDescriptor) ChangeAccess(access);
        if (!_bufferIndices.TryGetValue(buffer, out int resource) ||
            !_resourceAccessLists.TryGetValue((GraphAccessTargetKind.Buffer, resource), out List<int>? accesses)) return;
        foreach (int row in accesses)
            if (_accesses[row].BufferDescriptor && _accesses[row].Identity.IsValid)
                ChangeAccess(_accesses[row].Identity);
    }

    private List<ResolvedBufferRead> _previousBufferReads = [];
    private int _rangeReadStart;

    private readonly record struct ResolvedBufferRead(
        BufferRangeRead Read, int PassIndex, int ResourceIndex, ulong BufferSize);

    private bool PrepareBufferRangeReads(int firstAccess, out int accessCount)
    {
        _bufferReads.Clear();
        accessCount = firstAccess;
        foreach (BufferRangeRead read in _frame.BufferRangeReads)
        {
            if (read.Ranges.Count == 0) continue;
            int resource = ResolveBuffer(read.Buffer);
            _bufferReads.Add(new ResolvedBufferRead(read, ResolvePass(read.Pass), resource,
                _buffers[resource].Description.Size));
            accessCount = checked(accessCount + 1);
        }
        bool changed = _rangeReadStart != firstAccess || _accesses.Count != accessCount ||
            !CollectionsMarshal.AsSpan(_bufferReads).SequenceEqual(
                CollectionsMarshal.AsSpan(_previousBufferReads));
        _rangeReadStart = firstAccess;
        if (changed)
            _analysisChanged = true;
        return changed;
    }

    private void BuildBufferRangeReads(bool changed)
    {
        int index = _rangeReadStart;
        foreach (ResolvedBufferRead resolved in _bufferReads)
        {
            if (changed)
            {
                foreach (BufferRange range in resolved.Read.Ranges.Ranges)
                    _ = GraphStructureIndex.ResolveRange(range, resolved.BufferSize);
                SetAccessRow(index, new FrameResourceAccess
                {
                    Pass = resolved.Read.Pass,
                    Target = resolved.Read.Buffer,
                    TargetKind = GraphAccessTargetKind.Buffer,
                    Mode = GraphAccessMode.Read,
                    Coverage = WriteCoverage.Partial,
                    Sync = resolved.Read.Sync,
                    Access = resolved.Read.Access,
                    BufferRange = new(0, resolved.BufferSize),
                    SharedBufferRanges = resolved.Read.Ranges,
                    PassIndex = resolved.PassIndex,
                    ResourceIndex = resolved.ResourceIndex,
                });
            }
            index++;
        }
        (_bufferReads, _previousBufferReads) = (_previousBufferReads, _bufferReads);
    }

    private readonly HashSet<GraphBufferInput> _changedBufferInputs = [];
    private readonly Dictionary<GraphBufferInput, HashSet<ParameterConnection>> _bufferInputParameters = [];

    internal void ChangeBufferInput(GraphBufferInput input) => _changedBufferInputs.Add(input);

    private void RefreshBufferInputParameters()
    {
        foreach (GraphBufferInput input in _changedBufferInputs)
        {
            if (!_bufferInputParameters.TryGetValue(input, out HashSet<ParameterConnection>? connections)) continue;
            int buffer = ResolveBuffer(_frame.ResolveBufferInput(input).Buffer.Value);
            foreach (ParameterConnection connection in connections)
            {
                if (connection.Primary != buffer)
                {
                    RemoveParameterConnection(_resourceParameterConnections, (GraphAccessTargetKind.Buffer, connection.Primary), connection);
                    connection.Primary = buffer;
                    AddParameterConnection(_resourceParameterConnections, (GraphAccessTargetKind.Buffer, buffer), connection);
                }
                _parameterValueChanges.Add(connection);
            }
        }
        _changedBufferInputs.Clear();
    }

    private readonly HashSet<GraphParameterInput> _changedParameterInputs = [];
    private readonly Dictionary<GraphParameterInput, HashSet<ParameterConnection>> _parameterInputConnections = [];

    internal void ChangeParameterInput(GraphParameterInput input) => _changedParameterInputs.Add(input);

    private void RefreshParameterInputs()
    {
        foreach (GraphParameterInput input in _changedParameterInputs)
            if (_parameterInputConnections.TryGetValue(input, out HashSet<ParameterConnection>? connections))
                _parameterValueChanges.UnionWith(connections);
        _changedParameterInputs.Clear();
    }
}

