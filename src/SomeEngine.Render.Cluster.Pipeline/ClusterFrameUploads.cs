using System.Collections.Immutable;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Retains each declared upload by its graph identity and writes admitted generations.</summary>
internal sealed partial class ClusterFrameUploads : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly int _generationCount;
    private readonly Dictionary<GraphBufferId, UploadBuffer> _buffers = [];
    private readonly List<BufferChange> _bufferChanges = [];
    private readonly List<ValueChange> _valueChanges = [];
    private readonly List<UploadBuffer> _frameBuffers = [];
    private int _frameGeneration = -1;
    private bool _editing;
    private bool _writing;
    private bool _disposed;

    internal ClusterFrameUploads(IGraphicsBackend backend, Device device, int generationCount)
    {
        _backend = backend;
        _device = device;
        _generationCount = generationCount;
    }

    internal void BeginDeclarations()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_editing || _frameGeneration >= 0)
            throw new InvalidOperationException("Cluster upload data is already being edited or used by a frame.");
        _editing = true;
        _originalUniformBuffer = _uniformBuffer;
        _originalUniformSize = _uniformSize;
        _originalUniformFree = _uniformFree;
    }

    internal GraphBufferId Declare(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> value,
        BufferUsages usages, string label) => Declare(ref edit, ClusterUploadData.FromBytes(value), usages, label);

    internal GraphBufferId Declare(ref RenderGraphEdit edit, ClusterUploadData value,
        BufferUsages usages, string label)
    {
        RequireEdit();
        var description = new BufferDesc(checked((ulong)value.Length), usages | BufferUsages.CopySource, label);
        GraphBufferId id = edit.DeclareExternalBuffer(description, MemoryType.Upload);
        var buffer = new UploadBuffer(_backend, _device, id, description, _generationCount, value);
        _buffers.Add(id, buffer);
        _bufferChanges.Add(new(id, null, buffer));
        return id;
    }

    internal void SetValue(ref RenderGraphEdit edit, GraphBufferId id, scoped ReadOnlySpan<byte> value)
    {
        RequireEdit();
        UploadBuffer previous = _buffers[id];
        int alignment = (previous.Description.Usages & BufferUsages.Constant) != 0
            ? checked((int)Math.Max(1u, _device.Capabilities.Limits.ConstantBufferAlignment)) : 1;
        int size = checked((value.Length + alignment - 1) / alignment * alignment);
        if (size == value.Length)
        {
            if (previous.Value.ContentEquals(value)) return;
            SetChangedValue(ref edit, id, ClusterUploadData.FromBytes(value), [new BufferRange(0, checked((ulong)size))]);
            return;
        }
        Span<byte> padded = size <= 4096 ? stackalloc byte[size] : new byte[size];
        padded.Clear();
        value.CopyTo(padded);
        if (previous.Value.ContentEquals(padded)) return;
        SetChangedValue(ref edit, id, ClusterUploadData.FromBytes(padded), [new BufferRange(0, checked((ulong)size))]);
    }

    internal void Remove(ref RenderGraphEdit edit, GraphBufferId id)
    {
        RequireEdit();
        UploadBuffer buffer = _buffers[id];
        edit.Remove(id);
        _buffers.Remove(id);
        _bufferChanges.Add(new(id, buffer, null));
    }

    internal void SetChangedValue(GraphBufferId id, ClusterUploadData value, scoped ReadOnlySpan<BufferRange> changes)
    {
        if (_frameGeneration >= 0 || _editing)
            throw new InvalidOperationException("Upload values can change only between frames and edits.");
        _buffers[id].SetValue(value, changes);
    }

    internal void SetChangedValue(ref RenderGraphEdit edit, GraphBufferId id,
        byte[] value, scoped ReadOnlySpan<BufferRange> changes) =>
        SetChangedValue(ref edit, id, ClusterUploadData.FromBytes(value), changes);

    internal void SetChangedValue(ref RenderGraphEdit edit, GraphBufferId id,
        ClusterUploadData value, scoped ReadOnlySpan<BufferRange> changes)
    {
        RequireEdit();
        UploadBuffer previous = _buffers[id];
        if (ReferenceEquals(previous.Value, value) && changes.IsEmpty) return;
        if (previous.Value.Length != value.Length)
        {
            BufferDesc description = previous.Description with { Size = checked((ulong)value.Length) };
            var buffer = new UploadBuffer(_backend, _device, id, description, _generationCount, value);
            try { edit.SetBufferDescription(id, description, MemoryType.Upload); }
            catch { buffer.Dispose(); throw; }
            _buffers[id] = buffer;
            _bufferChanges.Add(new(id, previous, buffer));
            return;
        }
        _valueChanges.Add(previous.CaptureValue());
        previous.SetValue(value, changes);
    }

    internal void CompleteDeclarations(SomeEngine.RenderGraph.RenderGraph graph)
    {
        _editing = false;
        foreach (BufferChange change in _bufferChanges)
            if (change.Before is { } buffer) graph.RetireAfterSubmittedFrames(buffer);
        _bufferChanges.Clear();
        _valueChanges.Clear();
        _uniformChanges.Clear();
    }

    internal void CancelDeclarations()
    {
        if (!_editing) return;
        _uniformBuffer = _originalUniformBuffer;
        _uniformSize = _originalUniformSize;
        _uniformFree = _originalUniformFree;
        foreach ((GraphBufferInput input, UniformData? previous) in _uniformChanges)
        {
            if (previous is null) _uniforms.Remove(input);
            else _uniforms[input] = previous;
        }
        _uniformChanges.Clear();
        for (int index = _valueChanges.Count - 1; index >= 0; index--)
        {
            var change = _valueChanges[index];
            change.Buffer.RestoreValue(change);
        }
        List<Exception>? errors = null;
        for (int index = _bufferChanges.Count - 1; index >= 0; index--)
        {
            BufferChange change = _bufferChanges[index];
            if (change.Before is { } buffer) _buffers[change.Id] = buffer;
            else _buffers.Remove(change.Id);
            try { change.After?.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        _bufferChanges.Clear();
        _valueChanges.Clear();
        _editing = false;
        if (errors is not null) throw new AggregateException("Cluster upload edit cleanup failed.", errors);
    }

    internal void BeginFrame(int generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_editing || _frameGeneration >= 0)
            throw new InvalidOperationException("Cluster upload data is already being edited or used by a frame.");
        _frameGeneration = generation;
        _writing = true;
        foreach (UploadBuffer buffer in _buffers.Values) buffer.Bound = false;
        foreach (UniformData uniform in _uniforms.Values) uniform.Bound = false;
    }

    internal void WriteAndBind(ref RenderGraphFrame frame, int generation,
        scoped ReadOnlySpan<byte> value, GraphBufferId id)
    {
        if (!_writing || generation != _frameGeneration)
            throw new InvalidOperationException("Cluster upload writes require the active generation.");
        _buffers[id].WriteAndBind(ref frame, generation, value);
    }

    internal void CompleteFrame(ref RenderGraphFrame frame)
    {
        if (!_writing) throw new InvalidOperationException("Cluster upload writes are not active.");
        CompleteUniforms(ref frame);
        foreach (UploadBuffer buffer in _buffers.Values)
        {
            if (!buffer.Bound) buffer.Bind(ref frame, _frameGeneration);
            _frameBuffers.Add(buffer);
        }
        _writing = false;
    }

    internal void FinishFrame(int generation, RenderGraphFrameResult result)
    {
        if (_writing || generation != _frameGeneration)
            throw new InvalidOperationException("Cluster upload results require the active generation.");
        foreach (UploadBuffer buffer in _frameBuffers)
            buffer.StoreStates(generation, result.GetBufferStates(buffer.Id));
        DiscardFrame();
    }

    internal void CancelFrame() => DiscardFrame();

    internal void DiscardFrame()
    {
        _frameBuffers.Clear();
        _frameGeneration = -1;
        _writing = false;
    }

    private void RequireEdit()
    {
        if (!_editing) throw new InvalidOperationException("Cluster upload changes require a graph edit.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancelDeclarations();
        List<Exception>? errors = null;
        foreach (UploadBuffer buffer in _buffers.Values)
        {
            try { buffer.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        _buffers.Clear();
        _uniforms.Clear();
        DiscardFrame();
        _disposed = true;
        if (errors is not null) throw new AggregateException("Cluster upload cleanup failed.", errors);
    }

    private readonly record struct BufferChange(GraphBufferId Id, UploadBuffer? Before, UploadBuffer? After);
    private readonly record struct ValueChange(UploadBuffer Buffer, ClusterUploadData Value, ImmutableList<BufferRange>[] Ranges);

    private sealed class UploadBuffer : IDisposable
    {
        private Buffer?[] _buffers;
        private MappedBuffer[] _mappings;
        private BufferBoundaryState[][] _states;
        private ImmutableList<BufferRange>[] _changedRanges;
        private readonly int[] _bufferByGeneration;

        internal UploadBuffer(IGraphicsBackend backend, Device device, GraphBufferId id,
            in BufferDesc description, int generations, ClusterUploadData value)
        {
            Id = id;
            Description = description;
            Value = value;
            _buffers = new Buffer?[generations];
            _mappings = new MappedBuffer[generations];
            _states = new BufferBoundaryState[generations][];
            _changedRanges = new ImmutableList<BufferRange>[generations];
            _bufferByGeneration = new int[generations];
            Array.Fill(_bufferByGeneration, -1);
            try
            {
                for (int generation = 0; generation < generations; generation++)
                {
                    Buffer buffer = backend.CreateBuffer(device,
                        description with { Label = $"{description.Label} {generation}" }, MemoryType.Upload);
                    _buffers[generation] = buffer;
                    _mappings[generation] = backend.Map(buffer, MapType.Write, new BufferRange(0, description.Size));
                    _states[generation] = [new BufferBoundaryState(new BufferRange(0, description.Size),
                        buffer.InitialSync, buffer.InitialAccess, ResourceContentState.Defined)];
                    Value.CopyTo(_mappings[generation].Bytes);
                    _mappings[generation].Flush(new BufferRange(0, description.Size));
                    _changedRanges[generation] = [];
                }
            }
            catch { Dispose(); throw; }
        }

        internal GraphBufferId Id { get; }
        internal BufferDesc Description { get; }
        internal ClusterUploadData Value { get; private set; }
        internal bool Bound { get; set; }

        internal ValueChange CaptureValue() => new(this, Value, _changedRanges);
        internal void RestoreValue(ValueChange change) => (Value, _changedRanges) = (change.Value, change.Ranges);

        internal void SetValue(ClusterUploadData value, scoped ReadOnlySpan<BufferRange> changes)
        {
            if ((ulong)value.Length != Description.Size)
                throw new InvalidOperationException("Upload values must match the retained buffer size.");
            var ranges = new ImmutableList<BufferRange>[_changedRanges.Length];
            for (int generation = 0; generation < ranges.Length; generation++)
            {
                ranges[generation] = _changedRanges[generation];
                foreach (BufferRange range in changes)
                    ranges[generation] = IncludeRange(ranges[generation], range);
            }
            Value = value;
            _changedRanges = ranges;
        }

        private static readonly IComparer<BufferRange> RangeStarts =
            Comparer<BufferRange>.Create(static (left, right) => left.Offset.CompareTo(right.Offset));

        private static ImmutableList<BufferRange> IncludeRange(ImmutableList<BufferRange> ranges, BufferRange value)
        {
            if (value.Size == 0) return ranges;
            ulong start = value.Offset;
            ulong end = checked(start + value.Size);
            int index = ranges.BinarySearch(value, RangeStarts);
            if (index < 0) index = ~index;
            if (index > 0 && ranges[index - 1].Offset + ranges[index - 1].Size >= start) index--;
            while (index < ranges.Count && ranges[index].Offset <= end)
            {
                BufferRange overlap = ranges[index];
                start = Math.Min(start, overlap.Offset);
                end = Math.Max(end, checked(overlap.Offset + overlap.Size));
                ranges = ranges.RemoveAt(index);
            }
            return ranges.Insert(index, new BufferRange(start, end - start));
        }

        internal void WriteAndBind(ref RenderGraphFrame frame, int generation, scoped ReadOnlySpan<byte> value)
        {
            _bufferByGeneration[generation] = -1;
            int bufferIndex = FindWritableBuffer();
            // Retain the obligation to restore the default if a later frame does not override it.
            _changedRanges[bufferIndex] = [new BufferRange(0, Description.Size)];
            Write(bufferIndex, value);
            BindResource(ref frame, generation, bufferIndex);
        }

        internal void Bind(ref RenderGraphFrame frame, int generation)
        {
            // The admitted generation has finished its earlier GPU reads. Other
            // generations can keep reading any buffer whose value is unchanged.
            _bufferByGeneration[generation] = -1;
            int bufferIndex = 0;
            while (bufferIndex < _changedRanges.Length && !_changedRanges[bufferIndex].IsEmpty) bufferIndex++;
            if (bufferIndex == _changedRanges.Length) bufferIndex = FindWritableBuffer();
            foreach (BufferRange range in _changedRanges[bufferIndex])
            {
                int offset = checked((int)range.Offset);
                MappedBuffer mapping = _mappings[bufferIndex];
                Value.CopyTo(mapping.Bytes.Slice(offset, checked((int)range.Size)), offset);
                mapping.Flush(range);
            }
            _changedRanges[bufferIndex] = [];
            BindResource(ref frame, generation, bufferIndex);
        }

        private int FindWritableBuffer()
        {
            for (int bufferIndex = 0; bufferIndex < _buffers.Length; bufferIndex++)
                if (!_bufferByGeneration.Contains(bufferIndex)) return bufferIndex;
            throw new InvalidOperationException("Cluster upload generations still reference every buffer.");
        }

        private void BindResource(ref RenderGraphFrame frame, int generation, int bufferIndex)
        {
            frame.BindExternalBuffer(Id, _buffers[bufferIndex]!, _states[bufferIndex]);
            _bufferByGeneration[generation] = bufferIndex;
            Bound = true;
        }

        internal void StoreStates(int generation, ReadOnlySpan<BufferBoundaryState> states)
        {
            if (!states.IsEmpty) _states[_bufferByGeneration[generation]] = states.ToArray();
        }

        private void Write(int generation, ReadOnlySpan<byte> value)
        {
            if (checked((ulong)value.Length) != Description.Size)
                throw new InvalidOperationException("Cluster upload bytes do not match the declared buffer size.");
            MappedBuffer mapping = _mappings[generation];
            value.CopyTo(mapping.Bytes);
            mapping.Flush(new BufferRange(0, Description.Size));
        }

        public void Dispose()
        {
            List<Exception>? errors = null;
            for (int generation = _buffers.Length - 1; generation >= 0; generation--)
            {
                try { _mappings[generation].Dispose(); }
                catch (Exception error) { (errors ??= []).Add(error); }
                try { _buffers[generation]?.Dispose(); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            _buffers = [];
            _mappings = [];
            _states = [];
            _changedRanges = [];
            if (errors is not null) throw new AggregateException("Cluster upload buffer cleanup failed.", errors);
        }
    }
}
