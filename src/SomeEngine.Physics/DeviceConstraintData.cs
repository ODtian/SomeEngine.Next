using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceConstraintBinding<TData>(DeviceConstraintData<TData> Data, int Index) : ITrackedComponent
    where TData : unmanaged;

[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct DeviceConstraintMetadata
{
    public uint EntityIndex, EntityGeneration, Active, InputVersion;
}

/// <summary>Reads original parameters and entity references into a concrete device constraint representation.</summary>
public interface IDeviceConstraintSource<TComponent, TData> : IJobComponentResources<TComponent>
    where TComponent : struct, IComponent where TData : unmanaged
{
    static virtual ReadOnlySpan<ChunkAccess> Components => [];
    static virtual SystemAccesses AdditionalAccesses => new SystemAccesses().ReadRandom<DeviceBodyBinding>();
    static virtual void CollectResources(SystemChunk chunk, ref JobResourceAccessBuilder resources) { }
    static virtual bool HasChangedInputs(SystemChunk chunk, World world, ulong since) => false;
    void Write(SystemChunk chunk, World world, int start, scoped ReadLookup<DeviceBodyBinding> bodies, Span<TData> output);

}

/// <summary>Physical parameters and entity identities, shared by the selected device solvers.</summary>
public sealed class DeviceConstraintData<TData> : IDisposable, IDevicePhysicsSubmission where TData : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly string _bindingName;
    private readonly DeviceInputPages _upload, _metadataUpload;
    private readonly BufferUav _view, _metadataView;
    private readonly CommandContext _commands;
    private bool _initialized;
    private readonly bool[] _allocatedRows;
    private readonly ConcurrentDictionary<Entity, int> _inputRows = new();
    private readonly Stack<int> _freeRows = [];
    private int _nextRow;
    private readonly uint[] _inputRevisions;
    internal static readonly int Stride = Unsafe.SizeOf<TData>();

    public DeviceConstraintData(IGraphicsBackend backend, Device device, int capacity, string bindingName, DeviceContactMemory? memory = null)
    {
        _backend = backend; _device = device; Capacity = capacity; _bindingName = bindingName; Memory = memory;
        _allocatedRows = new bool[capacity];
        _inputRevisions = new uint[capacity];
        var dataDescription = new BufferDesc((ulong)capacity * (ulong)Stride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, bindingName);
        var metadataDescription = new BufferDesc((ulong)capacity * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, bindingName + " metadata");
        Data = memory is null ? backend.CreateBuffer(device, dataDescription) : memory.CreateBuffer(dataDescription, (uint)Stride);
        Metadata = memory is null ? backend.CreateBuffer(device, metadataDescription) : memory.CreateBuffer(metadataDescription, 16);
        _view = backend.CreateBufferUav(device, new(Data, new(0, Data.Info.Size), StructureStride: (uint)Stride));
        _metadataView = backend.CreateBufferUav(device, new(Metadata, new(0, Metadata.Info.Size), StructureStride: 16));
        _upload = new(backend, device, capacity, Stride, bindingName + " upload");
        _metadataUpload = new(backend, device, capacity, 16, bindingName + " metadata upload");
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: bindingName + " transfer"));
    }

    public int Capacity { get; }
    public DeviceContactMemory? Memory { get; }
    public int RowCount => Memory?.Rows ?? Capacity;
    public Buffer Data { get; }
    public Buffer Metadata { get; }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    SomeEngine.Graphics.Queue IDevicePhysicsSubmission.Queue => _backend.GetQueue(_device, QueueType.Compute);
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add(_bindingName, [ResourceBinding.WritableBuffer(_view)]);
        bindings.Add(_bindingName + "Metadata", [ResourceBinding.WritableBuffer(_metadataView)]);
    }
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(Data), 0, (long)Data.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(Metadata), 0, (long)Metadata.Info.Size)];
    public UploadDeviceConstraintInputs<TData> UploadInputs() => new(this);
    public WriteDeviceConstraintInputs<TComponent, TData, TSource> WriteInputs<TComponent, TSource>(
        World world, Query rows, TSource source, int rowsPerChunk, ReadOnlySpan<JobResourceAccess> extraAccesses = default)
        where TComponent : struct, IComponent where TSource : struct, IDeviceConstraintSource<TComponent, TData> =>
        new(world, rows, this, source, rowsPerChunk, extraAccesses);

    internal int InputRows(int index) => _upload.RowsRemaining(index);
    internal Span<TData> InputValues(int index, int count) => MemoryMarshal.Cast<byte, TData>(_upload.Range(index, count));
    internal void WriteInputMetadata(int index, Entity entity) => WriteInputMetadata(index, entity, ++_inputRevisions[index]);

    internal void WriteInput(int index, Entity entity, in TData data, uint inputVersion = 0)
    {
        MemoryMarshal.Write(_upload.Row(index), in data);
        WriteInputMetadata(index, entity, inputVersion);
    }
    internal void WriteInputMetadata(int index, Entity entity, uint inputVersion)
    {
        Volatile.Write(ref _allocatedRows[index], true);
        Memory?.EnsureRows(index + 1);
        var metadata = new DeviceConstraintMetadata
        { EntityIndex = (uint)entity.Id, EntityGeneration = (uint)entity.Generation, Active = 1, InputVersion = inputVersion };
        _inputRows[entity] = index;
        MemoryMarshal.Write(_metadataUpload.Row(index), in metadata);
    }
    internal void RetireInput(Entity entity)
    {
        if (!_inputRows.TryRemove(entity, out int index)) return;
        DeviceConstraintMetadata value = MemoryMarshal.Read<DeviceConstraintMetadata>(_metadataUpload.ReadRow(index));
        if (value.EntityIndex == (uint)entity.Id && value.EntityGeneration == (uint)entity.Generation) ClearInputMetadata(index);
    }
    internal void WriteInputField<TField>(int index, int offset, in TField value) where TField : unmanaged =>
        MemoryMarshal.Write(_upload.Row(index).Slice(offset, Unsafe.SizeOf<TField>()), in value);
    internal void ClearInputMetadata(int index) => _metadataUpload.Row(index).Clear();
    internal void CollectUploadWrite(int index, ref JobResourceAccessBuilder resources)
    {
        resources.Write(_upload, (long)index * Stride, Stride);
        resources.Write(_metadataUpload, (long)index * 16, 16);
        resources.Write(_allocatedRows.AsMemory(index, 1));
        resources.Write(_inputRevisions.AsMemory(index, 1));
    }
    internal JobHandle ScheduleUpload(Scheduler scheduler, JobHandle dependency)
    {
        return DevicePhysicsJobs.Schedule(scheduler, this, GetInputAccesses(scheduler), dependency);
    }
    internal JobResourceAccess[] GetInputAccesses(Scheduler scheduler, bool writeInputs = false) =>
        [JobResourceAccess.Write(scheduler.GetResourceToken(Data), 0, (long)Data.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(Metadata), 0, (long)Metadata.Info.Size),
            writeInputs ? JobResourceAccess.Write(scheduler.GetResourceToken(_upload), 0, (long)(ulong)_upload.Size)
                : JobResourceAccess.Read(scheduler.GetResourceToken(_upload), 0, (long)(ulong)_upload.Size),
            writeInputs ? JobResourceAccess.Write(scheduler.GetResourceToken(_metadataUpload), 0, (long)(ulong)_metadataUpload.Size)
                : JobResourceAccess.Read(scheduler.GetResourceToken(_metadataUpload), 0, (long)(ulong)_metadataUpload.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
    internal JobResourceAccess[] GetUploadWriteAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Write(scheduler.GetResourceToken(_upload), 0, _upload.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_metadataUpload), 0, _metadataUpload.Size), GetRowAllocationAccess(scheduler)];
    internal JobResourceAccess[] GetDeviceWriteAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Write(scheduler.GetResourceToken(Data), 0, (long)Data.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(Metadata), 0, (long)Metadata.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_metadataUpload), 0, _metadataUpload.Size)];
    internal void BeginDeviceWrite(CommandContext commands)
    {
        Memory?.EnsureRows(1);
        if (_initialized) return;
        if (Memory?.UsesSparseBuffers != true)
        {
            _backend.Barrier(commands, new BufferBarrier(Data, Data.InitialSync, PipelineSync.ComputeShading,
                Data.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.Barrier(commands, new BufferBarrier(Metadata, Metadata.InitialSync, PipelineSync.Copy,
                Metadata.InitialAccess, ResourceAccess.CopyDestination));
            _metadataUpload.Zero(commands, Metadata);
            _backend.Barrier(commands, new BufferBarrier(Metadata, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
        _initialized = true;
    }
    internal JobResourceAccess GetRowAllocationAccess(Scheduler scheduler) => JobResourceAccess.Write(scheduler, _allocatedRows);
    internal int ReserveRow()
    {
        lock (_allocatedRows) return ReserveRowCore();
    }
    internal void ReserveRows(Span<int> rows)
    {
        lock (_allocatedRows)
            for (int index = 0; index < rows.Length; index++) rows[index] = ReserveRowCore();
        Memory?.EnsureRows(_nextRow);
    }
    private int ReserveRowCore()
    {
        while (_freeRows.TryPop(out int freed))
        {
            if (Volatile.Read(ref _allocatedRows[freed])) continue;
            _allocatedRows[freed] = true;
            return freed;
        }
        while (Volatile.Read(ref _allocatedRows[_nextRow])) _nextRow++;
        int row = _nextRow++;
        _allocatedRows[row] = true;
        return row;
    }
    internal void ReleaseRow(int row)
    {
        lock (_allocatedRows) { _allocatedRows[row] = false; _freeRows.Push(row); }
    }
    internal void RetainRow(int row) => Volatile.Write(ref _allocatedRows[row], true);

    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        Memory?.EnsureRows(1);
        _backend.Begin(_commands, new(0, 0, 8));
        if (!_initialized && Memory?.UsesSparseBuffers != true)
        {
            _backend.Barrier(_commands, new BufferBarrier(Metadata, Metadata.InitialSync, PipelineSync.Copy,
                Metadata.InitialAccess, ResourceAccess.CopyDestination));
            _metadataUpload.Zero(_commands, Metadata);
            _backend.Barrier(_commands, new BufferBarrier(Metadata, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
        CopyWritten(_upload, Data); CopyWritten(_metadataUpload, Metadata);
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void CopyWritten(DeviceInputPages inputs, Buffer destination)
    {
        bool initialized = _initialized || Memory?.Contains(destination) == true || ReferenceEquals(destination, Metadata);
        _backend.Barrier(_commands, new BufferBarrier(destination, initialized ? PipelineSync.ComputeShading : destination.InitialSync,
            PipelineSync.Copy, initialized ? ResourceAccess.UnorderedAccess : destination.InitialAccess, ResourceAccess.CopyDestination));
        inputs.CopyWrittenPages(_commands, destination);
        _backend.Barrier(_commands, new BufferBarrier(destination, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }

    internal QueueCompletion SubmitInputRanges(ReadOnlySpan<BufferRange> dataRanges, ReadOnlySpan<BufferRange> metadataRanges)
    {
        Memory?.EnsureRows(1);
        _backend.Begin(_commands, new(0, 0, 8));
        if (!_initialized && Memory?.UsesSparseBuffers != true)
        {
            _backend.Barrier(_commands, new BufferBarrier(Metadata, Metadata.InitialSync, PipelineSync.Copy,
                Metadata.InitialAccess, ResourceAccess.CopyDestination));
            _metadataUpload.Zero(_commands, Metadata);
            _backend.Barrier(_commands, new BufferBarrier(Metadata, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
        Copy(_upload, Data, dataRanges); Copy(_metadataUpload, Metadata, metadataRanges);
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void Copy(DeviceInputPages upload, Buffer destination, ReadOnlySpan<BufferRange> ranges)
    {
        if (ranges.IsEmpty)
        {
            if (!_initialized && Memory?.Contains(destination) != true && !ReferenceEquals(destination, Metadata))
                _backend.Barrier(_commands, new BufferBarrier(destination, destination.InitialSync, PipelineSync.ComputeShading,
                    destination.InitialAccess, ResourceAccess.UnorderedAccess));
            return;
        }
        _backend.Barrier(_commands, new BufferBarrier(destination, _initialized || Memory?.Contains(destination) == true || ReferenceEquals(destination, Metadata) ? PipelineSync.ComputeShading : destination.InitialSync,
            PipelineSync.Copy, _initialized || Memory?.Contains(destination) == true || ReferenceEquals(destination, Metadata) ? ResourceAccess.UnorderedAccess : destination.InitialAccess, ResourceAccess.CopyDestination));
        upload.Copy(_commands, destination, ranges);
        _backend.Barrier(_commands, new BufferBarrier(destination, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }
    public void Dispose()
    {
        _inputRows.Clear(); _commands.Dispose(); _upload.Dispose(); _metadataUpload.Dispose();
        _view.Dispose(); _metadataView.Dispose(); Data.Dispose(); Metadata.Dispose();
    }
}

public readonly struct UploadDeviceConstraintInputs<TData>(DeviceConstraintData<TData> data) : IDevicePhysicsStage where TData : unmanaged
{
    public object InputKey => data;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        data.ScheduleUpload(WorldJobs.GetScheduler(context.World), dependency);
}
public readonly struct DeviceConstraintUploadResources<TData> : IJobComponentResources<DeviceConstraintBinding<TData>> where TData : unmanaged
{
    public static void Collect(in DeviceConstraintBinding<TData> component, ref JobResourceAccessBuilder resources) =>
        component.Data.CollectUploadWrite(component.Index, ref resources);
}
public readonly struct DeviceConstraintReferenceResources<TData> : IJobComponentResources<DeviceConstraintBinding<TData>> where TData : unmanaged
{
    public static void Collect(in DeviceConstraintBinding<TData> component, ref JobResourceAccessBuilder resources) { }
}
