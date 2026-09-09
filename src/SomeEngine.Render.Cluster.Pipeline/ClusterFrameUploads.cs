using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Owns the generation-local upload buffers behind the retained Cluster graph's external slots.
/// Graph structure keeps stable resource identities; a frame only writes values and binds the
/// admitted generation.
/// </summary>
internal sealed class ClusterFrameUploads : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly int _generationCount;
    private readonly List<UploadBuffer> _uploadBuffers = [];
    private readonly Dictionary<string, List<UploadBuffer>> _buffersByLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _declarationOccurrences = new(StringComparer.Ordinal);
    private readonly Dictionary<GraphBufferId, UploadBuffer> _buffersById = [];
    private readonly List<DeclarationChange> _declarationChanges = [];
    private readonly List<UploadBuffer> _deactivatedBuffers = [];
    private readonly List<UploadBuffer> _frameBuffers = [];
    private int _pendingGeneration = -1;
    private bool _declaring;
    private bool _writing;
    private bool _disposed;
    private GraphPassId _finalizationPass;
    private GraphPassId _previousFinalizationPass;

    internal GraphPassId FinalizationPass => _finalizationPass;

    internal ClusterFrameUploads(
        IGraphicsBackend backend,
        Device device,
        int generationCount)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationCount);
        _generationCount = generationCount;
    }

    internal void BeginDeclarations()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writing)
            throw new InvalidOperationException("Cluster frame uploads are being written.");
        if (_declaring)
            throw new InvalidOperationException("Cluster frame upload declarations are already active.");
        _declarationOccurrences.Clear();
        _declarationChanges.Clear();
        _deactivatedBuffers.Clear();
        _previousFinalizationPass = _finalizationPass;
        _declaring = true;
    }

    internal void RemoveUnusedDeclarations(ref RenderGraphEdit edit)
    {
        if (!_declaring)
            throw new InvalidOperationException("Cluster frame upload declarations are not active.");
        foreach ((string label, int activeCount) in _declarationOccurrences)
        {
            if (!_buffersByLabel.TryGetValue(label, out List<UploadBuffer>? slots))
                continue;
            for (int occurrence = activeCount; occurrence < slots.Count; occurrence++)
            {
                UploadBuffer slot = slots[occurrence];
                if (!slot.Active)
                    continue;
                edit.Remove(slot.Id);
                slot.Active = false;
                _deactivatedBuffers.Add(slot);
            }
        }
    }

    internal void CompleteDeclarations(SomeEngine.RenderGraph.RenderGraph graph)
    {
        _declaring = false;
        foreach (UploadBuffer buffer in _deactivatedBuffers)
        {
            _uploadBuffers.Remove(buffer);
            _buffersById.Remove(buffer.Id);
            graph.RetireAfterSubmittedFrames(buffer);
        }
        _declarationOccurrences.Clear();
        _declarationChanges.Clear();
        _deactivatedBuffers.Clear();
    }

    internal void CancelDeclarations()
    {
        if (!_declaring) return;
        _finalizationPass = _previousFinalizationPass;
        for (int index = _declarationChanges.Count - 1; index >= 0; index--)
        {
            DeclarationChange change = _declarationChanges[index];
            if (!_buffersByLabel.TryGetValue(change.Label, out List<UploadBuffer>? values))
                continue;
            if (change.Previous is { } previous)
                values[change.Occurrence] = previous;
            else if (change.Occurrence == values.Count - 1)
                values.RemoveAt(change.Occurrence);
            if (values.Count == 0)
                _buffersByLabel.Remove(change.Label);
            _uploadBuffers.Remove(change.Current);
            _buffersById.Remove(change.Current.Id);
            change.Current.Dispose();
        }
        for (int index = 0; index < _deactivatedBuffers.Count; index++)
            _deactivatedBuffers[index].Active = true;
        _deactivatedBuffers.Clear();
        _declarationChanges.Clear();
        _declaring = false;
        _declarationOccurrences.Clear();
    }

    internal GraphBufferId Declare(
        ref RenderGraphEdit edit,
        scoped ReadOnlySpan<byte> initialValue,
        BufferUsages usages,
        string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_declaring)
            throw new InvalidOperationException("Cluster frame upload declarations are not active.");
        if (_writing)
            throw new InvalidOperationException("Cluster frame uploads are being written.");
        if (initialValue.IsEmpty)
            throw new ArgumentException("A Cluster upload slot cannot be empty.", nameof(initialValue));
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        BufferDesc description = new(
            checked((ulong)initialValue.Length),
            usages | BufferUsages.CopySource,
            label);
        int occurrence = _declarationOccurrences.GetValueOrDefault(label);
        _declarationOccurrences[label] = checked(occurrence + 1);
        if (_buffersByLabel.TryGetValue(label, out List<UploadBuffer>? existing) &&
            occurrence < existing.Count)
        {
            UploadBuffer retained = existing[occurrence];
            if (retained.Active && retained.HasDescription(description) && retained.HasInitialValue(initialValue))
                return retained.Id;
        }

        GraphBufferId id = edit.DeclareExternalBuffer(description, MemoryType.Upload);
        var slot = new UploadBuffer(_backend, _device, id, description, _generationCount);
        try
        {
            slot.Initialize(initialValue);
            _uploadBuffers.Add(slot);
            _buffersById.Add(id, slot);
            if (!_buffersByLabel.TryGetValue(label, out List<UploadBuffer>? values))
            {
                values = [];
                _buffersByLabel.Add(label, values);
            }
            UploadBuffer? previous = null;
            if (occurrence < values.Count)
            {
                previous = values[occurrence];
                if (previous.Active)
                {
                    edit.Remove(previous.Id);
                    previous.Active = false;
                    _deactivatedBuffers.Add(previous);
                }
                values[occurrence] = slot;
            }
            else
                values.Add(slot);
            _declarationChanges.Add(new DeclarationChange(
                label,
                occurrence,
                previous,
                slot));
            return id;
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    internal void BeginFrame(int generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writing)
            throw new InvalidOperationException("Cluster frame uploads are already being written.");
        if (_pendingGeneration >= 0)
            throw new InvalidOperationException("The previous Cluster frame uploads are awaiting commit or discard.");
        if ((uint)generation >= (uint)_generationCount)
            throw new ArgumentOutOfRangeException(nameof(generation));
        _frameBuffers.Clear();
        _pendingGeneration = generation;
        foreach (UploadBuffer slot in _uploadBuffers)
            slot.Bound = false;
        _writing = true;
    }

    internal void WriteAndBind(ref RenderGraphFrame frame, int generation,
        scoped ReadOnlySpan<byte> value, GraphBufferId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writing || generation != _pendingGeneration)
            throw new InvalidOperationException("Cluster frame uploads are not being written for this generation.");
        if (!_buffersById.TryGetValue(id, out UploadBuffer? buffer) || !buffer.Active)
            throw new InvalidOperationException("The Cluster upload buffer is not active in this graph version.");
        buffer.WriteAndBind(ref frame, generation, value);
    }

    internal GraphPassId DeclareFinalization(
        ref RenderGraphEdit frame,
        GraphBufferId instanceData,
        ulong instanceDataSize,
        GraphBufferId instanceMetadata,
        ulong instanceMetadataSize,
        GraphTextureId previousHiZ,
        GraphTextureId currentHiZ,
        TextureSubresourceRange hiZRange,
        bool finalizeCurrentHiZ)
    {
        UploadBuffer[] active = _uploadBuffers.Where(static value => value.Active).ToArray();
        var resources = new UploadFinalizationResource[active.Length];
        for (int index = 0; index < resources.Length; index++)
            resources[index] = new UploadFinalizationResource(active[index].Id, active[index].Size);
        if (_finalizationPass != default)
            frame.Remove(_finalizationPass);
        var data = new UploadFinalizationPassData(
            resources,
            instanceData,
            instanceDataSize,
            instanceMetadata,
            instanceMetadataSize,
            previousHiZ,
            currentHiZ,
            hiZRange,
            finalizeCurrentHiZ);
        GraphPassId finalization = frame.AddGeneralFramePass(
            "Finalize Cluster frame resource endpoints",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            UploadFinalizationPassData.Declare,
            UploadFinalizationPassData.Record);
        _finalizationPass = finalization;
        return finalization;
    }

    internal void CompleteFrame(ref RenderGraphFrame frame)
    {
        if (!_writing || _finalizationPass == default)
            throw new InvalidOperationException("Cluster upload finalization was not declared.");
        // Constant uploads participate every frame even when no value changes.
        // They use the same generation and endpoint tracking as dynamic values.
        foreach (UploadBuffer slot in _uploadBuffers)
        {
            if (!slot.Active)
                continue;
            if (!slot.Bound)
                slot.Bind(ref frame, _pendingGeneration);
            _frameBuffers.Add(slot);
        }
        _writing = false;
    }

    internal void CancelFrame()
    {
        _frameBuffers.Clear();
        _pendingGeneration = -1;
        _writing = false;
    }

    internal void Commit(int generation, ReadOnlySpan<QueueCompletion> completions)
    {
        if (_writing || generation != _pendingGeneration)
            throw new InvalidOperationException("Cluster frame upload commit does not match the pending generation.");
        QueueCompletion completion = FindCompletion(completions, QueueType.Graphics);
        foreach (UploadBuffer slot in _frameBuffers)
            slot.Commit(generation, completion);
        _frameBuffers.Clear();
        _pendingGeneration = -1;
    }

    internal void DiscardFrame()
    {
        _frameBuffers.Clear();
        _pendingGeneration = -1;
        _writing = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        for (int index = _uploadBuffers.Count - 1; index >= 0; index--)
        {
            try { _uploadBuffers[index].Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _uploadBuffers.Clear();
        _buffersById.Clear();
        _buffersByLabel.Clear();
        _declarationOccurrences.Clear();
        _declarationChanges.Clear();
        _deactivatedBuffers.Clear();
        _frameBuffers.Clear();
        _pendingGeneration = -1;
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private sealed class UploadBuffer : IDisposable
    {
        private byte[] _initialValue = [];
        private Buffer?[] _buffers;
        private MappedBuffer[] _mappings;
        private BufferBoundaryState[] _endpoints;

        internal UploadBuffer(
            IGraphicsBackend backend,
            Device device,
            GraphBufferId id,
            in BufferDesc description,
            int generationCount)
        {
            Id = id;
            Description = description;
            _buffers = new Buffer?[generationCount];
            _mappings = new MappedBuffer[generationCount];
            _endpoints = new BufferBoundaryState[generationCount];
            try
            {
                for (int generation = 0; generation < generationCount; generation++)
                {
                    Buffer buffer = backend.CreateBuffer(
                        device,
                        description with { Label = $"{description.Label} {generation}" },
                        MemoryType.Upload);
                    _buffers[generation] = buffer;
                    _mappings[generation] = backend.Map(buffer, MapType.Write, new BufferRange(0, description.Size));
                    _endpoints[generation] = new BufferBoundaryState(
                        new BufferRange(0, description.Size),
                        buffer.InitialSync,
                        buffer.InitialAccess,
                        ResourceContentState.Defined);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal GraphBufferId Id { get; }

        internal bool Active { get; set; } = true;
        internal bool Bound { get; set; }

        internal ulong Size => Description.Size;

        private BufferDesc Description { get; }

        internal bool HasDescription(in BufferDesc description) => Description == description;

        internal bool HasInitialValue(scoped ReadOnlySpan<byte> value) => value.SequenceEqual(_initialValue);

        internal void Initialize(scoped ReadOnlySpan<byte> value)
        {
            _initialValue = value.ToArray();
            for (int generation = 0; generation < _buffers.Length; generation++)
                Write(generation, value);
        }

        internal void WriteAndBind(
            ref RenderGraphFrame frame,
            int generation,
            scoped ReadOnlySpan<byte> value)
        {
            Write(generation, value);
            Bind(ref frame, generation);
        }

        internal void Bind(ref RenderGraphFrame frame, int generation)
        {
            Buffer buffer = _buffers[generation] ?? throw new ObjectDisposedException(nameof(UploadBuffer));
            Bound = true;
            frame.BindExternalBuffer(
                Id,
                buffer,
                [_endpoints[generation]]);
        }

        internal void Commit(int generation, QueueCompletion completion)
        {
            if ((uint)generation >= (uint)_endpoints.Length)
                throw new ArgumentOutOfRangeException(nameof(generation));
            _endpoints[generation] = new BufferBoundaryState(
                new BufferRange(0, Description.Size),
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                ResourceContentState.Defined,
                completion.Queue,
                completion);
        }

        private Buffer Write(int generation, scoped ReadOnlySpan<byte> value)
        {
            if ((uint)generation >= (uint)_buffers.Length || _buffers[generation] is not { } buffer)
                throw new InvalidOperationException("The Cluster upload generation is unavailable.");
            if (checked((ulong)value.Length) != Description.Size)
            {
                throw new InvalidOperationException(
                    $"Cluster upload '{Description.Label}' changed from {Description.Size} to {value.Length} bytes.");
            }
            BufferRange range = new(0, Description.Size);
            MappedBuffer mapping = _mappings[generation];
            value.CopyTo(mapping.Bytes);
            mapping.Flush(range);
            return buffer;
        }

        public void Dispose()
        {
            List<Exception>? failures = null;
            for (int generation = _buffers.Length - 1; generation >= 0; generation--)
            {
                try { _mappings[generation].Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
                try { _buffers[generation]?.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
                _buffers[generation] = null;
            }
            _buffers = [];
            _mappings = [];
            _endpoints = [];
            if (failures is not null)
                throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    private readonly record struct DeclarationChange(
        string Label,
        int Occurrence,
        UploadBuffer? Previous,
        UploadBuffer Current);

    private readonly record struct UploadFinalizationResource(GraphBufferId Id, ulong Size);

    private readonly record struct UploadFinalizationPassData(
        UploadFinalizationResource[] Resources,
        GraphBufferId InstanceData,
        ulong InstanceDataSize,
        GraphBufferId InstanceMetadata,
        ulong InstanceMetadataSize,
        GraphTextureId PreviousHiZ,
        GraphTextureId CurrentHiZ,
        TextureSubresourceRange HiZRange,
        bool FinalizeCurrentHiZ)
    {
        internal static void Declare(
            ref PassDefinition access,
            ref UploadFinalizationPassData data)
        {
            foreach (UploadFinalizationResource resource in data.Resources)
            {
                _ = access.Read(
                    resource.Id,
                    new BufferRange(0, resource.Size),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource);
            }
            _ = access.Read(
                data.InstanceData,
                new BufferRange(0, data.InstanceDataSize),
                PipelineSync.AllShading,
                ResourceAccess.ShaderResource);
            _ = access.Read(
                data.InstanceMetadata,
                new BufferRange(0, data.InstanceMetadataSize),
                PipelineSync.AllShading,
                ResourceAccess.ConstantBuffer);
            _ = access.Read(
                data.PreviousHiZ,
                data.HiZRange,
                PipelineSync.ComputeShading,
                ResourceAccess.ShaderResource,
                TextureLayout.ShaderResource);
            if (data.FinalizeCurrentHiZ)
            {
                _ = access.Read(
                    data.CurrentHiZ,
                    data.HiZRange,
                    PipelineSync.ComputeShading,
                    ResourceAccess.ShaderResource,
                    TextureLayout.ShaderResource);
            }
        }

        internal static void Record(
            ref GeneralPassCommandScope commands,
            in UploadFinalizationPassData data)
        {
            _ = commands;
            _ = data;
        }
    }

    private static QueueCompletion FindCompletion(
        ReadOnlySpan<QueueCompletion> completions,
        QueueType type)
    {
        foreach (ref readonly QueueCompletion completion in completions)
            if (completion.Queue.Type == type)
                return completion;
        throw new InvalidOperationException($"Cluster frame uploads require a {type} Queue completion.");
    }
}
