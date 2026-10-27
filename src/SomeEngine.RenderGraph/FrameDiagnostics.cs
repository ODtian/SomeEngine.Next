namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private void PublishDiagnostics()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Diagnostics);
        RenderGraphDiagnosticsHandler? handler = _frame.Options.Diagnostics;
        RenderGraphStatisticsHandler? statisticsHandler = _frame.Options.Statistics;
        if (handler is null && statisticsHandler is null) return;

        int[]? positions = handler is null ? null : new int[_passes.Count];
        if (positions is not null)
        {
            int position = 0;
            foreach (int pass in _executionOrder.Nodes) positions[pass] = position++;
        }

        List<RenderGraphPassDiagnostic>? passes = handler is null
            ? null : new(_passIndices.Count + _frame.DynamicPasses.Count);
        int declaredCount = 0;
        int liveCount = 0;
        for (int index = 0; index < _passes.Count; index++)
        {
            FramePass pass = _passes[index];
            if (!pass.Identity.IsValid) continue;
            declaredCount++;
            if (_live[index]) liveCount++;
            passes?.Add(new RenderGraphPassDiagnostic(
                new GraphPassId(pass.Identity),
                pass.Label,
                pass.Kind,
                pass.Enabled,
                _live[index],
                pass.DeclarationOrdinal,
                _live[index] ? positions![index] : -1,
                pass.Queue));
        }

        RenderGraphBufferDiagnostic[]? buffers = handler is null ? null : new RenderGraphBufferDiagnostic[_buffers.Count];
        ulong resourceBytes = 0;
        for (int index = 0; index < _buffers.Count; index++)
        {
            FrameBuffer buffer = _buffers[index];
            if (buffer.LastPass >= 0 &&
                buffer.Ownership == RenderGraphResourceOwnership.GraphOwned &&
                buffer.Lifetime == RenderGraphResourceLifetime.PerFrame)
            {
                resourceBytes = checked(resourceBytes + buffer.Requirements.Size);
            }
            if (buffers is not null)
                buffers[index] = new RenderGraphBufferDiagnostic(
                    new GraphBufferId(buffer.Identity),
                    buffer.Description.Label,
                    buffer.Ownership,
                    buffer.Lifetime,
                    buffer.Description.Size,
                    buffer.Placement.Offset,
                    buffer.Placement.Size,
                    buffer.FirstPass < 0 ? -1 : positions![buffer.FirstPass],
                    buffer.LastPass < 0 ? -1 : positions![buffer.LastPass]);
        }

        RenderGraphTextureDiagnostic[]? textures = handler is null ? null : new RenderGraphTextureDiagnostic[_textures.Count];
        for (int index = 0; index < _textures.Count; index++)
        {
            FrameTexture texture = _textures[index];
            if (texture.LastPass >= 0 &&
                texture.Ownership == RenderGraphResourceOwnership.GraphOwned &&
                texture.Lifetime == RenderGraphResourceLifetime.PerFrame)
            {
                resourceBytes = checked(resourceBytes + texture.Requirements.Size);
            }
            if (textures is not null)
                textures[index] = new RenderGraphTextureDiagnostic(
                    new GraphTextureId(texture.Identity),
                    texture.Label,
                    texture.Ownership,
                    texture.Lifetime,
                    texture.Width,
                    texture.Height,
                    texture.Format,
                    texture.Placement.Offset,
                    texture.Placement.Size,
                    texture.FirstPass < 0 ? -1 : positions![texture.FirstPass],
                    texture.LastPass < 0 ? -1 : positions![texture.LastPass]);
        }

        RenderGraphQueryPoolDiagnostic[]? queryPools = null;
        RenderGraphShaderTableDiagnostic[]? shaderTables = null;
        if (handler is not null)
        {
            queryPools = new RenderGraphQueryPoolDiagnostic[_queryPools.Count];
            for (int index = 0; index < queryPools.Length; index++)
            {
                FrameQueryPool pool = _queryPools[index];
                queryPools[index] = new(new GraphQueryPoolId(pool.Identity), pool.Resource.Label,
                    pool.Resource.Description.Count, pool.Resource.Description.Type);
            }
            shaderTables = new RenderGraphShaderTableDiagnostic[_shaderTables.Count];
            for (int index = 0; index < shaderTables.Length; index++)
                shaderTables[index] = new(new GraphRayTracingShaderTableId(_shaderTables[index].Identity),
                    _shaderTables[index].Resource.Label);
        }
        List<RenderGraphAccessDiagnostic>? accesses = handler is null ? null : new(_accesses.Count);
        int accessCount = 0;
        for (int index = 0; index < _accesses.Count; index++)
        {
            FrameResourceAccess access = _accesses[index];
            if (!access.Connected) continue;
            if (accesses is null)
            {
                accessCount += access.BufferRanges.Count;
                continue;
            }
            foreach (BufferRange range in access.BufferRanges)
                accesses.Add(new RenderGraphAccessDiagnostic(
                    new GraphPassId(_passes[access.PassIndex].Identity),
                    access.TargetKind,
                    access.ResourceIndex,
                    access.Mode,
                    access.Coverage,
                    access.Sync,
                    access.Access,
                    range,
                    access.TextureRange,
                    access.TextureLayout,
                    access.ResultContents,
                    access.BufferContentScope, access.QueryRange));
        }

        List<RenderGraphDependencyDiagnostic>? dependencies = handler is null ? null : new();
        int dependencyCount = 0;
        Dictionary<int, int>? consumerByPredecessor = null;
        for (int consumer = 0; consumer < _passes.Count; consumer++)
        {
            if (!_live[consumer]) continue;
            foreach (int predecessor in _valuePredecessors[consumer])
                AddDependency(predecessor, consumer, RenderGraphDependencyKind.Value);
            foreach (int predecessor in _predecessors[consumer])
                AddDependency(predecessor, consumer, RenderGraphDependencyKind.Execution);
            foreach (int predecessor in _physicalPredecessors[consumer])
                AddDependency(predecessor, consumer, RenderGraphDependencyKind.Physical);
        }

        List<RenderGraphBarrierDiagnostic>? barriers = handler is null ? null : new();
        int barrierCount = 0;
        for (int pass = 0; pass < _passes.Count; pass++)
        {
            if (!_live[pass]) continue;
            if (barriers is null)
            {
                barrierCount += _beforeBufferBarriers[pass].Count + _afterBufferBarriers[pass].Count +
                    _beforeTextureBarriers[pass].Count + _afterTextureBarriers[pass].Count +
                    _acquires[pass].Count + _releases[pass].Count +
                    Math.Max(_aliasBeforeResources[pass].Count, _aliasAfterResources[pass].Count);
                continue;
            }
            GraphPassId passId = new(_passes[pass].Identity);
            foreach (BufferBarrier barrier in _beforeBufferBarriers[pass])
                barriers.Add(new RenderGraphBarrierDiagnostic(passId, RenderGraphBarrierKind.Buffer, barrier.Phase));
            foreach (BufferBarrier barrier in _afterBufferBarriers[pass])
                barriers.Add(new RenderGraphBarrierDiagnostic(passId, RenderGraphBarrierKind.Buffer, barrier.Phase));
            foreach (TextureBarrier barrier in _beforeTextureBarriers[pass])
                barriers.Add(new RenderGraphBarrierDiagnostic(passId, RenderGraphBarrierKind.Texture, barrier.Phase));
            foreach (TextureBarrier barrier in _afterTextureBarriers[pass])
                barriers.Add(new RenderGraphBarrierDiagnostic(passId, RenderGraphBarrierKind.Texture, barrier.Phase));
            foreach (QueueAcquire _ in _acquires[pass])
                barriers.Add(new RenderGraphBarrierDiagnostic(passId, RenderGraphBarrierKind.QueueAcquire, BarrierPhase.Complete));
            foreach (QueueRelease _ in _releases[pass])
                barriers.Add(new RenderGraphBarrierDiagnostic(passId, RenderGraphBarrierKind.QueueRelease, BarrierPhase.Complete));
            for (int index = 0; index < Math.Max(_aliasBeforeResources[pass].Count, _aliasAfterResources[pass].Count); index++)
                barriers.Add(new RenderGraphBarrierDiagnostic(passId, RenderGraphBarrierKind.Aliasing, BarrierPhase.Complete));
        }

        var statistics = new RenderGraphStatistics(
            passes?.Count ?? declaredCount,
            liveCount,
            _executionOrder.Count,
            _buffers.Count,
            _textures.Count,
            accesses?.Count ?? accessCount,
            dependencies?.Count ?? dependencyCount,
            barriers?.Count ?? barrierCount,
            _queuePasses.Count,
            resourceBytes,
            _frame.Slot.TransientResources!.OccupiedBytes,
            _frame.Slot.TransientResources.CapacityBytes,
            _frame.Graph.HeapByteBudget.CommittedBytes)
        {
            AnalysisReused = _reuseAnalysis,
            AnalyzedPassCount = _analyzedPasses.Count(index => _passes[index].Identity.IsValid),
            RebuiltRowCount = _rebuiltRowCount,
        };
        if (handler is not null)
        {
            var view = new RenderGraphDiagnosticsView(
                _frame.StructureVersion,
                CollectionsMarshal.AsSpan(passes),
                buffers,
                textures,
                CollectionsMarshal.AsSpan(accesses),
                CollectionsMarshal.AsSpan(dependencies),
                CollectionsMarshal.AsSpan(barriers),
                statistics, queryPools, shaderTables);
            handler(in view);
        }
        statisticsHandler?.Invoke(in statistics);
        return;

        void AddDependency(int predecessor, int consumer, RenderGraphDependencyKind kind)
        {
            if (!_live[predecessor]) return;
            if (kind == RenderGraphDependencyKind.Physical)
            {
                ref int previousConsumer = ref CollectionsMarshal.GetValueRefOrAddDefault(
                    consumerByPredecessor ??= [], predecessor, out bool found);
                if (found && previousConsumer == consumer) return;
                previousConsumer = consumer;
            }
            dependencyCount++;
            dependencies?.Add(new RenderGraphDependencyDiagnostic(
                new GraphPassId(_passes[predecessor].Identity),
                new GraphPassId(_passes[consumer].Identity),
                kind));
        }
    }
}
