using SomeEngine.Graphics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Commits the contact columns together as their shared row range grows.</summary>
public sealed class DeviceContactMemory : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly SparseResources? _sparse;
    private readonly List<Column> _columns = [];
    private readonly List<Heap> _heaps = [];
    private readonly CommandContext? _commands;
    private readonly Buffer? _zeros;
    private volatile int _rows;
    private volatile bool _columnsChanged;

    public DeviceContactMemory(IGraphicsBackend backend, Device device, int capacity)
    {
        _backend = backend; _device = device; Capacity = capacity;
        if (backend.TryGetCapability(device, out SparseResources? sparse) && sparse is not null && sparse.BufferSupported)
        {
            _sparse = sparse;
            _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact memory"));
            _zeros = backend.CreateBuffer(device, new(sparse.TileSizeInBytes, BufferUsages.CopySource), MemoryType.Upload);
            using MappedBuffer mapping = backend.Map(_zeros, MapType.Write, BufferRange.Whole);
            mapping.Bytes.Clear(); mapping.Flush(mapping.Range);
        }
    }

    public int Capacity { get; }
    public int Rows => _sparse is null ? Capacity : _rows;
    public bool UsesSparseBuffers => _sparse is not null;

    public Buffer CreateBuffer(in BufferDesc description, uint bytesPerContact, int extraRows = 0)
    {
        if (_sparse is null) return _backend.CreateBuffer(_device, description, MemoryType.DeviceLocal);
        Buffer buffer = _backend.CreateReservedBuffer(_device, description with { Usages = description.Usages | BufferUsages.CopyDestination });
        _columns.Add(new(buffer, bytesPerContact, extraRows, _backend.GetSparseResourceInfo(buffer).Alignment)); _columnsChanged = true;
        return buffer;
    }

    public bool Contains(Buffer buffer) => _sparse is not null && _columns.Any(column => ReferenceEquals(column.Buffer, buffer));

    public void EnsureRows(int count)
    {
        if (_sparse is null || count <= _rows && !_columnsChanged) return;
        if (count > Capacity) throw new ArgumentOutOfRangeException(nameof(count), count, $"Contact row count exceeds the configured capacity {Capacity}.");
        lock (_columns)
        {
            int rows = Math.Min(Capacity, (Math.Max(count, 1) + 255) / 256 * 256);
            if (count <= _rows && !_columnsChanged) return;
            if (count <= _rows) rows = _rows;
            var ranges = new List<(Column Column, uint Start, uint Count)>();
            foreach (Column column in _columns)
            {
                ulong tileBytes = column.TileBytes;
                ulong prefixBytes = (ulong)(Capacity + column.ExtraRows) * column.BytesPerContact;
                uint end = (uint)(((ulong)(rows + column.ExtraRows) * column.BytesPerContact + tileBytes - 1) / tileBytes);
                uint suffixStart = (uint)(prefixBytes / tileBytes);
                uint total = (uint)((column.Buffer.Info.Size + tileBytes - 1) / tileBytes);
                bool suffix = column.Buffer.Info.Size > prefixBytes;
                if (suffix) end = Math.Min(end, suffixStart);
                if (end > column.PrefixTiles) ranges.Add((column, column.PrefixTiles, end - column.PrefixTiles));
                if (suffix && !column.Initialized && total > suffixStart) ranges.Add((column, suffixStart, total - suffixStart));
                column.PrefixTiles = end;
            }
            ulong heapBytes = 0, alignment = 0;
            var heapOffsets = new List<ulong>(ranges.Count);
            foreach (var range in ranges)
            {
                ulong tileBytes = range.Column.TileBytes;
                heapBytes = (heapBytes + tileBytes - 1) / tileBytes * tileBytes;
                heapOffsets.Add(heapBytes / tileBytes); heapBytes += range.Count * tileBytes;
                alignment = Math.Max(alignment, tileBytes);
            }
            var waits = new List<QueueCompletion>();
            if (heapBytes != 0)
            {
                Heap heap = _backend.CreateHeap(_device, new((heapBytes + alignment - 1) / alignment * alignment,
                    alignment, MemoryType.DeviceLocal, HeapFlags.Buffers));
                _heaps.Add(heap);
                var mappings = new List<SparseMappingDesc>(ranges.Count);
                for (int index = 0; index < ranges.Count; index++)
                {
                    var range = ranges[index];
                    mappings.Add(new(range.Column.Buffer, new(new(range.Start, 0, 0, 0), 0, 0, 0, range.Count, false),
                        SparseMappingType.Mapped, heap, heapOffsets[index]));
                }
                int batch = (int)Math.Min((uint)mappings.Count, _sparse.MaximumMappingsPerCall);
                for (int first = 0; first < mappings.Count; first += batch)
                    waits.Add(_backend.UpdateSparseMappings(_backend.GetQueue(_device, QueueType.Graphics),
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mappings).Slice(first, Math.Min(batch, mappings.Count - first))));
            }
            _backend.Begin(_commands!, new(0, 0, 8));
            foreach (Column column in _columns)
            {
                bool writes = ranges.Any(range => ReferenceEquals(range.Column, column));
                if (!writes && column.Initialized) continue;
                _backend.Barrier(_commands!, new BufferBarrier(column.Buffer,
                    column.Initialized ? PipelineSync.ComputeShading : column.Buffer.InitialSync,
                    writes ? PipelineSync.Copy : PipelineSync.ComputeShading,
                    column.Initialized ? ResourceAccess.UnorderedAccess : column.Buffer.InitialAccess,
                    writes ? ResourceAccess.CopyDestination : ResourceAccess.UnorderedAccess));
                foreach (var range in ranges)
                    if (ReferenceEquals(range.Column, column))
                        for (ulong offset = (ulong)range.Start * column.TileBytes,
                            end = Math.Min(column.Buffer.Info.Size, (ulong)(range.Start + range.Count) * column.TileBytes);
                            offset < end; offset += _zeros!.Info.Size)
                            _backend.CopyBuffer(_commands!, new(_zeros!, 0, column.Buffer, offset, Math.Min(_zeros!.Info.Size, end - offset)));
                if (writes) _backend.Barrier(_commands!, new BufferBarrier(column.Buffer, PipelineSync.Copy,
                    PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
                column.Initialized = true;
            }
            using RecordedCommands recorded = _backend.End(_commands!);
            _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(waits), [], [recorded], [], []));
            _rows = rows; _columnsChanged = false;
        }
    }

    private sealed class Column(Buffer buffer, uint bytesPerContact, int extraRows, ulong tileBytes)
    {
        internal readonly Buffer Buffer = buffer;
        internal readonly uint BytesPerContact = bytesPerContact;
        internal readonly int ExtraRows = extraRows;
        internal readonly ulong TileBytes = tileBytes;
        internal uint PrefixTiles;
        internal bool Initialized;
    }
    public void Dispose()
    {
        _commands?.Dispose(); _zeros?.Dispose();
        foreach (Heap heap in _heaps) heap.Dispose();
    }
}
