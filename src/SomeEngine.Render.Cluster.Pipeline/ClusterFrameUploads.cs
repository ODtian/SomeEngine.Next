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
    private readonly List<Slot> _slots = [];
    private int _writeCursor;
    private bool _writing;
    private bool _disposed;

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

    internal GraphBufferId Declare(
        ref RenderGraphEdit edit,
        scoped ReadOnlySpan<byte> initialValue,
        BufferUsages usages,
        string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writing)
            throw new InvalidOperationException("Cluster frame uploads are being written.");
        if (initialValue.IsEmpty)
            throw new ArgumentException("A Cluster upload slot cannot be empty.", nameof(initialValue));
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        BufferDesc description = new(
            checked((ulong)initialValue.Length),
            usages,
            label);
        GraphBufferId id = edit.DeclareExternalBuffer(description, MemoryType.Upload);
        var slot = new Slot(_backend, _device, id, description, _generationCount);
        try
        {
            slot.Initialize(initialValue);
            _slots.Add(slot);
            return id;
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    internal void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writing)
            throw new InvalidOperationException("Cluster frame uploads are already being written.");
        _writeCursor = 0;
        _writing = true;
    }

    internal GraphBufferId WriteAndBind(
        ref RenderGraphFrame frame,
        int generation,
        scoped ReadOnlySpan<byte> value,
        string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writing)
            throw new InvalidOperationException("Cluster frame uploads are not being written.");
        if ((uint)_writeCursor >= (uint)_slots.Count)
            throw new InvalidOperationException("Cluster frame upload structure changed after installation.");
        Slot slot = _slots[_writeCursor++];
        slot.WriteAndBind(ref frame, generation, value, label);
        return slot.Id;
    }

    internal void CompleteFrame()
    {
        if (!_writing)
            throw new InvalidOperationException("Cluster frame uploads are not being written.");
        _writing = false;
        if (_writeCursor != _slots.Count)
        {
            throw new InvalidOperationException(
                $"Cluster frame wrote {_writeCursor} of {_slots.Count} retained upload slots.");
        }
    }

    internal void CancelFrame()
    {
        _writeCursor = 0;
        _writing = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        for (int index = _slots.Count - 1; index >= 0; index--)
        {
            try { _slots[index].Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _slots.Clear();
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private sealed class Slot : IDisposable
    {
        private readonly IGraphicsBackend _backend;
        private Buffer?[] _buffers;

        internal Slot(
            IGraphicsBackend backend,
            Device device,
            GraphBufferId id,
            in BufferDesc description,
            int generationCount)
        {
            _backend = backend;
            Id = id;
            Description = description;
            _buffers = new Buffer?[generationCount];
            try
            {
                for (int generation = 0; generation < generationCount; generation++)
                {
                    _buffers[generation] = backend.CreateBuffer(
                        device,
                        description with { Label = $"{description.Label} {generation}" },
                        MemoryType.Upload);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal GraphBufferId Id { get; }

        private BufferDesc Description { get; }

        internal void Initialize(scoped ReadOnlySpan<byte> value)
        {
            for (int generation = 0; generation < _buffers.Length; generation++)
                Write(generation, value);
        }

        internal void WriteAndBind(
            ref RenderGraphFrame frame,
            int generation,
            scoped ReadOnlySpan<byte> value,
            string label)
        {
            if (!string.Equals(Description.Label, label, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cluster upload slot '{Description.Label}' was replayed as '{label}'.");
            }
            Buffer buffer = Write(generation, value);
            BufferRange range = new(0, Description.Size);
            frame.BindExternalBuffer(
                Id,
                buffer,
                [new BufferBoundaryState(
                    range,
                    buffer.InitialSync,
                    buffer.InitialAccess,
                    ResourceContentState.Defined)]);
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
            using MappedBuffer mapping = _backend.Map(buffer, MapType.Write, range);
            value.CopyTo(mapping.Bytes);
            mapping.Flush(range);
            return buffer;
        }

        public void Dispose()
        {
            List<Exception>? failures = null;
            for (int generation = _buffers.Length - 1; generation >= 0; generation--)
            {
                try { _buffers[generation]?.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
                _buffers[generation] = null;
            }
            _buffers = [];
            if (failures is not null)
                throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }
}
