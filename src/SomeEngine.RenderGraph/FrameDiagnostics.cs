namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private void PublishDiagnostics()
    {
        RenderGraphDiagnosticsHandler? handler = _frame.Options.Diagnostics;
        if (handler is null) return;

        var passes = new RenderGraphPassDiagnostic[_passes.Length];
        int liveCount = 0;
        for (int index = 0; index < _passes.Length; index++)
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

        var buffers = new RenderGraphBufferDiagnostic[_buffers.Length];
        ulong logicalBytes = 0;
        ulong physicalBytes = 0;
        for (int index = 0; index < _buffers.Length; index++)
        {
            FrameBuffer buffer = _buffers[index];
            if (buffer.LastUse >= 0 &&
                buffer.Ownership == RenderGraphResourceOwnership.GraphOwned &&
                buffer.Lifetime == RenderGraphResourceLifetime.PerFrame)
            {
                logicalBytes = checked(logicalBytes + buffer.Requirements.Size);
                physicalBytes = checked(physicalBytes + buffer.Placement.Size);
            }
            buffers[index] = new RenderGraphBufferDiagnostic(
                new GraphBufferId(buffer.Identity),
                buffer.Description.Label,
                buffer.Ownership,
                buffer.Lifetime,
                buffer.Description.Size,
                buffer.Placement.Offset,
                buffer.Placement.Size,
                buffer.FirstUse == int.MaxValue ? -1 : buffer.FirstUse,
                buffer.LastUse);
        }

        var textures = new RenderGraphTextureDiagnostic[_textures.Length];
        for (int index = 0; index < _textures.Length; index++)
        {
            FrameTexture texture = _textures[index];
            if (texture.LastUse >= 0 &&
                texture.Ownership == RenderGraphResourceOwnership.GraphOwned &&
                texture.Lifetime == RenderGraphResourceLifetime.PerFrame)
            {
                logicalBytes = checked(logicalBytes + texture.Requirements.Size);
                physicalBytes = checked(physicalBytes + texture.Placement.Size);
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
                texture.FirstUse == int.MaxValue ? -1 : texture.FirstUse,
                texture.LastUse);
        }

        var accesses = new RenderGraphAccessDiagnostic[_accesses.Length];
        for (int index = 0; index < _accesses.Length; index++)
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
                access.ResultContents);
        }

        var dependencies = new List<RenderGraphDependencyDiagnostic>();
        var seen = new HashSet<(int From, int To, RenderGraphDependencyKind Kind)>();
        for (int consumer = 0; consumer < _passes.Length; consumer++)
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
        for (int pass = 0; pass < _passes.Length; pass++)
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
            foreach (AliasingResource _ in _aliasBeforeResources[pass])
                barriers.Add(new RenderGraphBarrierDiagnostic(passId, RenderGraphBarrierKind.Aliasing, BarrierPhase.Complete));
        }

        var statistics = new RenderGraphStatistics(
            _passes.Length,
            liveCount,
            _schedule.Length,
            _buffers.Length,
            _textures.Length,
            _accesses.Length,
            dependencies.Count,
            barriers.Count,
            _queueLaneCount,
            logicalBytes,
            physicalBytes);
        var view = new RenderGraphDiagnosticsView(
            _frame.Graph.StructureVersion,
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
