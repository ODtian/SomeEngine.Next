using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Serialization;

public sealed partial class WorldSerializer
{
    private ChunkData[]? _capturedChunks;
    private ulong _captureNumber;
    private int _checkpointTableCount;
    private readonly List<CapturedTable> _capturedTables = [];
    private readonly List<(SavedService Binding, WorldServiceData Data)> _capturedServices = [];
    private ReadOnlySpan<CapturedTable> CheckpointTables => CollectionsMarshal.AsSpan(_capturedTables).Slice(0, _checkpointTableCount);
    private sealed class CapturedTable
    {
        private static readonly IComparer<SavedColumn> ColumnOrder = Comparer<SavedColumn>.Create(
            static (left, right) => left.Key.StableId.CompareTo(right.Key.StableId));
        internal ChunkData First = null!;
        internal SavedColumn[] Columns = [];
        internal readonly List<ChunkData> Chunks = [];
        internal int[] ColumnIndices = [];
        internal int ColumnCount;

        internal void Bind(ChunkData first, SerializationRegistry registry)
        {
            First = first;
            Chunks.Clear(); Chunks.Add(first);
            int count = first.Components.Length;
            if (Columns.Length < count) { Array.Resize(ref Columns, count); Array.Resize(ref ColumnIndices, count); }
            ColumnCount = 0;
            for (int i = 0; i < count; i++)
                if (registry.Columns.TryGetValue(first.Components[i].Id, out SavedColumn? column))
                {
                    Columns[ColumnCount] = column;
                    ColumnIndices[ColumnCount++] = i;
                }
            Array.Clear(Columns, ColumnCount, Columns.Length - ColumnCount);
            Array.Sort(Columns, ColumnIndices, 0, ColumnCount, ColumnOrder);
        }

        internal void Clear() { First = null!; Chunks.Clear(); Array.Clear(Columns); }
    }

    public void Save(WorldCheckpoint captured, IBufferWriter<byte> output,
        SavePurpose purpose = SavePurpose.RawCheckpoint)
        => Save(captured, output, new SerializeOptions(Purpose: purpose));

    public void Save(WorldCheckpoint checkpoint, IBufferWriter<byte> output, SerializeOptions options)
    {
        if (!options.Codecs.IsDefaultOrEmpty)
        {
            new WorldSerializer(SerializationRegistry.Bind(registry, options.Codecs))
                .Save(checkpoint, output, options with { Codecs = default });
            return;
        }
        ValidateWriteOptions(options);
        var writer = new DataWriter(output, options.ExternalReferences);
        Write(checkpoint, ref writer, options);
        writer.Flush();
    }
    public void Save(WorldCheckpoint captured, Stream output, SavePurpose purpose = SavePurpose.RawCheckpoint)
        => Save(captured, output, new SerializeOptions(Purpose: purpose));

    public void Save(WorldCheckpoint checkpoint, Stream output, SerializeOptions options)
    {
        if (!options.Codecs.IsDefaultOrEmpty)
        {
            new WorldSerializer(SerializationRegistry.Bind(registry, options.Codecs))
                .Save(checkpoint, output, options with { Codecs = default });
            return;
        }
        ValidateWriteOptions(options);
        var writer = new DataWriter(output, options.ExternalReferences);
        Write(checkpoint, ref writer, options);
        writer.Flush();
    }

    private void Select(WorldCheckpoint captured)
    {
        SynchronizeRegistry();
        foreach (SavedType type in _used) _seen[registry.GetOrder(type)] = false;
        _used.Clear(); _capturedServices.Clear();
        if (_seen.Length < registry.Types.Count)
        { Array.Resize(ref _seen, registry.Types.Count); Array.Resize(ref _fileTypes, registry.Types.Count); }
        if (!ReferenceEquals(_capturedChunks, captured.Chunks) || _captureNumber != captured.CaptureNumber)
        {
            _capturedChunks = null;
            int tableCount = 0;
            CapturedTable? current = null;
            foreach (ChunkData chunk in captured.Chunks.AsSpan(0, captured.ChunkCount))
            {
                if (chunk.Count == 0) continue;
                if (current is not null && ReferenceEquals(current.First.Types, chunk.Types)
                    && ReferenceEquals(current.First.Groups, chunk.Groups)) { current.Chunks.Add(chunk); continue; }
                if (tableCount == _capturedTables.Count) _capturedTables.Add(new());
                current = _capturedTables[tableCount++];
                current.Bind(chunk, registry);
            }
            for (int i = tableCount; i < _capturedTables.Count; i++) _capturedTables[i].Clear();
            _checkpointTableCount = tableCount;
            _captureNumber = captured.CaptureNumber;
            _capturedChunks = captured.Chunks;
        }
        foreach (CapturedTable table in CheckpointTables)
            foreach (SavedColumn column in table.Columns.AsSpan(0, table.ColumnCount)) _seen[registry.GetOrder(column)] = true;
        for (int type = 0; type < captured.Shared.Length; type++)
            if (captured.Shared[type] is not null && registry.Columns.TryGetValue(type, out SavedColumn? shared)) _seen[registry.GetOrder(shared)] = true;
        for (int type = 0; type < captured.Sparse.Length; type++)
            if (captured.Sparse[type] is not null && registry.Sparse.TryGetValue(type, out SavedSparse? sparse)) _seen[registry.GetOrder(sparse)] = true;
        for (int type = 0; type < captured.Resources.Capacity; type++)
            if (captured.Resources.Get(type) is { HasValue: true } && registry.Resources.TryGetValue(type, out SavedResource? resource)) _seen[registry.GetOrder(resource)] = true;
        foreach (WorldServiceData data in captured.Services.AsSpan(0, captured.ServiceCount))
            foreach (SavedService service in registry.Services)
                if (service.Matches(data))
                { _capturedServices.Add((service, data)); _seen[registry.GetOrder(service.Type)] = true; break; }
        foreach (SavedType type in registry.Types)
            if (_seen[registry.GetOrder(type)]) { _fileTypes[registry.GetOrder(type)] = _used.Count; _used.Add(type); }
    }

    private void SaveCaptured(WorldCheckpoint checkpoint, Stream output, SerializeOptions options)
    {
        var writer = new DataWriter(output, options.ExternalReferences);
        Write(checkpoint, ref writer, options, countsChecked: true);
        writer.Flush();
    }

    private void Write(WorldCheckpoint captured, ref DataWriter writer, SerializeOptions options, bool countsChecked = false)
    {
        Select(captured);
        if (!countsChecked)
        {
            CheckSparseMemberships(captured.Sparse, registry, options.MaximumSparseMemberships);
            if (options.MaximumTopologyRecords != 0)
            {
                long remaining = options.MaximumTopologyRecords;
                foreach (var service in _capturedServices)
                {
                    long count = service.Binding.CountRecords(captured, service.Data);
                    if (count > remaining) throw new InvalidOperationException("Topology records exceed the configured write limit.");
                    remaining -= count;
                }
            }
        }
        WriteHeader(ref writer, options.Purpose);
        writer.WriteByte((byte)captured.Progress); writer.WriteUInt64(captured.Version);
        writer.WriteInt32(captured.Used); writer.WriteInt32(captured.EntityCount); writer.WriteInt32(captured.FreeCount);
        for (int id = 1; id <= captured.Used; id++) writer.WriteInt32(captured.Records[id].Generation);
        for (int id = captured.FreeHead; id != 0; id = captured.Records[id].Row) writer.WriteInt32(id);
        int sharedCount = 0, sparseCount = 0, resourceCount = 0;
        foreach (SavedType type in _used)
            if (type is SavedShared) sharedCount++;
            else if (type is SavedSparse) sparseCount++;
            else if (type is SavedResource) resourceCount++;
        writer.WriteInt32(sharedCount);
        foreach (SavedType type in _used)
            if (type is SavedShared shared)
            { writer.WriteInt32(_fileTypes[registry.GetOrder(type)]); shared.WriteValues(ref writer, captured.Shared[type.Type]!); }
        writer.WriteInt32(_checkpointTableCount);
        foreach (CapturedTable table in CheckpointTables) WriteTable(ref writer, table);
        writer.WriteInt32(sparseCount);
        foreach (SavedType type in _used)
            if (type is SavedSparse sparse)
            { writer.WriteInt32(_fileTypes[registry.GetOrder(type)]); sparse.Write(ref writer, captured.Sparse[type.Type]!); }
        writer.WriteInt32(resourceCount);
        foreach (SavedType type in _used)
            if (type is SavedResource resource)
            { writer.WriteInt32(_fileTypes[registry.GetOrder(type)]); resource.Write(ref writer, captured.Resources.Get(type.Type)!); }
        long topologyBytesRemaining = options.MaximumTopologyPayloadBytes;
        WriteCapturedTopology(ref writer, options.MaximumTopologyPayloadBytes != 0, ref topologyBytesRemaining);
        WriteEvents(ref writer, captured.Events);
        writer.WriteInt32(_capturedServices.Count);
        foreach (var service in _capturedServices)
        {
            writer.WriteInt32(_fileTypes[registry.GetOrder(service.Binding.Type)]);
            bool bounded = options.MaximumTopologyPayloadBytes != 0 && service.Binding.Type.Storage is SavedStorage.Parent or SavedStorage.Endpoints;
            long start = writer.Position;
            if (bounded) writer.LimitBytes(topologyBytesRemaining);
            try { service.Binding.WriteData(ref writer, service.Data); }
            finally { if (bounded) writer.EndByteLimit(); }
            if (bounded) topologyBytesRemaining -= writer.Position - start;
        }
        writer.WriteInt32(captured.SystemCount);
        for (int i = 0; i < captured.SystemCount; i++)
        {
            writer.WriteInt32(captured.SystemVersionCounts[i] / 2);
            foreach (ulong version in captured.SystemVersions[i].AsSpan(0, captured.SystemVersionCounts[i])) writer.WriteUInt64(version);
        }
    }

    private void WriteTable(ref DataWriter writer, CapturedTable table)
    {
        ChunkData first = table.First;
        writer.WriteInt32(table.ColumnCount);
        foreach (SavedColumn column in table.Columns.AsSpan(0, table.ColumnCount)) writer.WriteInt32(_fileTypes[registry.GetOrder(column)]);
        int groupCount = 0;
        for (int i = 0; i < table.ColumnCount; i++)
            if (first.Columns[table.ColumnIndices[i]].GroupPlusOne != 0) groupCount++;
        writer.WriteInt32(groupCount);
        for (int i = 0; i < table.ColumnCount; i++)
        {
            int group = first.Columns[table.ColumnIndices[i]].GroupPlusOne;
            if (group == 0) continue;
            writer.WriteInt32(_fileTypes[registry.GetOrder(table.Columns[i])]); writer.WriteInt32(first.Groups[group - 1]);
        }
        writer.WriteInt32(table.Chunks.Count);
        foreach (ChunkData chunk in table.Chunks)
        {
            writer.WriteInt32(chunk.Count);
            writer.WriteEntities(chunk.Entities.AsSpan(0, chunk.Count));
            for (int i = 0; i < table.ColumnCount; i++)
            {
                SavedColumn binding = table.Columns[i];
                int source = table.ColumnIndices[i];
                ComponentType type = chunk.Components[source];
                ref ColumnEntry column = ref chunk.Columns[source];
                WriteMask(ref writer, column.Presence, chunk.Count);
                if (binding.Storage is not (SavedStorage.Parent or SavedStorage.Endpoints or SavedStorage.Children))
                    binding.Write(ref writer, ref column, chunk.Count);
                WriteMask(ref writer, chunk.Enabled is { } enabled ? enabled[source] : null, chunk.Count);
                SavedChanges.Write(ref writer, chunk.Changes is { } changes ? changes[source] : default,
                    chunk.Count, type.TracksWrites, type.TracksAdded);
            }
        }
    }

    private void WriteCapturedTopology(ref DataWriter writer, bool bounded, ref long bytesRemaining)
    {
        int count = 0;
        foreach (SavedType type in _used) if (type.Storage is SavedStorage.Parent or SavedStorage.Endpoints or SavedStorage.Children) count++;
        writer.WriteInt32(count);
        foreach (SavedType type in _used)
        {
            if (type.Storage is not (SavedStorage.Parent or SavedStorage.Endpoints or SavedStorage.Children)) continue;
            var binding = (SavedColumn)type;
            writer.WriteInt32(_fileTypes[registry.GetOrder(type)]);
            int rows = 0;
            foreach (CapturedTable table in CheckpointTables)
            {
                int column = Array.BinarySearch(table.First.Types.Types, type.Type);
                if (column < 0) continue;
                foreach (ChunkData chunk in table.Chunks)
                    if (chunk.Columns[column].Presence is null) rows += chunk.Count;
                    else for (int row = 0; row < chunk.Count; row++) if (chunk.Columns[column].Has(row)) rows++;
            }
            writer.WriteInt32(rows);
            long start = writer.Position;
            if (bounded) writer.LimitBytes(bytesRemaining);
            try
            {
                foreach (CapturedTable table in CheckpointTables)
                {
                    int column = Array.BinarySearch(table.First.Types.Types, type.Type);
                    if (column < 0) continue;
                    foreach (ChunkData chunk in table.Chunks)
                        binding.WriteEntityRows(ref writer, ref chunk.Columns[column], chunk.Entities.AsSpan(0, chunk.Count));
                }
            }
            finally { if (bounded) writer.EndByteLimit(); }
            if (bounded) bytesRemaining -= writer.Position - start;
            writer.WriteInt32(checked((int)(writer.Position - start)));
        }
    }

    public Task SaveAsync(World world, Stream output, SavePurpose purpose = SavePurpose.DurableSave,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
        => SaveAsync(world, output, new SerializeOptions(Purpose: purpose), progress, systems);

    public Task SaveAsync(World world, Stream output, SerializeOptions options,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
    {
        if (!options.Codecs.IsDefaultOrEmpty)
            return new WorldSerializer(SerializationRegistry.Bind(registry, options.Codecs))
                .SaveAsync(world, output, options with { Codecs = default }, progress, systems);
        Scheduler scheduler = JobSystem.Current;
        ValidateWriteOptions(options);
        var checkpoint = new WorldCheckpoint();
        try { checkpoint.Capture(world, registry, options, progress, systems); }
        catch { checkpoint.Dispose(); throw; }
        try
        {
            return scheduler.Schedule(new SaveStreamJob(registry, checkpoint, output, options)).CompleteAsync().AsTask();
        }
        catch { checkpoint.Dispose(); throw; }
    }

    private readonly struct SaveStreamJob(SerializationRegistry registry, WorldCheckpoint checkpoint,
        Stream output, SerializeOptions options) : IJob
    {
        public void Execute()
        {
            try { new WorldSerializer(registry).SaveCaptured(checkpoint, output, options); }
            finally { checkpoint.Dispose(); }
        }
    }
}
