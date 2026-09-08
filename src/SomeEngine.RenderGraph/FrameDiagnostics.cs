namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private void PublishDiagnostics()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Diagnostics);
        RenderGraphDiagnosticsHandler? handler = _frame.Options.Diagnostics;
        if (handler is null) return;

        var passes = new RenderGraphPassDiagnostic[_passes.Count];
        int liveCount = 0;
        for (int index = 0; index < _passes.Count; index++)
        {
            FramePass pass = _passes[index];
            if (_live[index]) liveCount++;
            passes[index] = new RenderGraphPassDiagnostic(
                new GraphPassId(pass.Identity),
                pass.Label,
                pass.Kind,
                pass.Enabled,
                _live[index],
                pass.DeclarationOrdinal,
                _live[index] ? pass.ScheduledOrdinal : -1,
                pass.Queue);
        }

        var buffers = new RenderGraphBufferDiagnostic[_buffers.Count];
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
            buffers[index] = new RenderGraphBufferDiagnostic(
                new GraphBufferId(buffer.Identity),
                buffer.Description.Label,
                buffer.Ownership,
                buffer.Lifetime,
                buffer.Description.Size,
                buffer.Placement.Offset,
                buffer.Placement.Size,
                buffer.FirstPass < 0 ? -1 : _scheduledPosition[buffer.FirstPass],
                buffer.LastPass < 0 ? -1 : _scheduledPosition[buffer.LastPass]);
        }

        var textures = new RenderGraphTextureDiagnostic[_textures.Count];
        for (int index = 0; index < _textures.Count; index++)
        {
            FrameTexture texture = _textures[index];
            if (texture.LastPass >= 0 &&
                texture.Ownership == RenderGraphResourceOwnership.GraphOwned &&
                texture.Lifetime == RenderGraphResourceLifetime.PerFrame)
            {
                resourceBytes = checked(resourceBytes + texture.Requirements.Size);
            }
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
                texture.FirstPass < 0 ? -1 : _scheduledPosition[texture.FirstPass],
                texture.LastPass < 0 ? -1 : _scheduledPosition[texture.LastPass]);
        }

        var accesses = new RenderGraphAccessDiagnostic[_accesses.Count];
        for (int index = 0; index < _accesses.Count; index++)
        {
            FrameResourceAccess access = _accesses[index];
            accesses[index] = new RenderGraphAccessDiagnostic(
                new GraphPassId(_passes[access.PassIndex].Identity),
                access.TargetKind,
                access.ResourceIndex,
                access.Mode,
                access.Coverage,
                access.Sync,
                access.Access,
                access.BufferRange,
                access.TextureRange,
                access.TextureLayout,
                access.ResultContents,
                access.BufferContentScope);
        }

        var dependencies = new List<RenderGraphDependencyDiagnostic>();
        var seen = new HashSet<(int From, int To, RenderGraphDependencyKind Kind)>();
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

        var barriers = new List<RenderGraphBarrierDiagnostic>();
        for (int pass = 0; pass < _passes.Count; pass++)
        {
            if (!_live[pass]) continue;
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
            _passes.Count,
            liveCount,
            _schedule.Length,
            _buffers.Count,
            _textures.Count,
            _accesses.Count,
            dependencies.Count,
            barriers.Count,
            _queueLastPass.Count,
            resourceBytes,
            _frame.Slot.TransientResources!.OccupiedBytes,
            _frame.Slot.TransientResources.CapacityBytes,
            _frame.Graph.HeapByteBudget.CommittedBytes)
        {
            AnalysisReused = _reuseAnalysis,
        };
        var view = new RenderGraphDiagnosticsView(
            _frame.StructureVersion,
            passes,
            buffers,
            textures,
            accesses,
            CollectionsMarshal.AsSpan(dependencies),
            CollectionsMarshal.AsSpan(barriers),
            statistics);
        handler(in view);
        return;

        void AddDependency(int predecessor, int consumer, RenderGraphDependencyKind kind)
        {
            if (!_live[predecessor] || !seen.Add((predecessor, consumer, kind))) return;
            dependencies.Add(new RenderGraphDependencyDiagnostic(
                new GraphPassId(_passes[predecessor].Identity),
                new GraphPassId(_passes[consumer].Identity),
                kind));
        }
    }
}
