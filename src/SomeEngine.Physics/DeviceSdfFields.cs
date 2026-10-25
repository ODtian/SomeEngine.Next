using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceSdfCapacity(int Fields, int Samples);
public readonly record struct DeviceSdfBinding(DeviceSdfFields Fields, int Index) : IComponent;

[StructLayout(LayoutKind.Sequential, Size = 80)]
public struct DeviceSdfInput
{
    public Vector3 Origin;
    public float Scale;
    public Vector3 CellSize;
    public uint SampleStart;
    public uint XCount, YCount, ZCount;
    public float TraceStepScale;
    public Vector3 Min;
    public uint FieldIndex;
    public Vector3 Max;
}

/// <summary>Owns shared field samples and concrete field metadata, referenced by the original SDF entities.</summary>
public sealed class DeviceSdfFields : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Buffer _fieldUpload;
    private readonly Buffer? _sampleUpload;
    private readonly BufferUav _fieldView;
    private readonly BufferUav? _sampleView;
    private readonly CommandContext _commands;
    private MappedBuffer _fieldMapping, _sampleMapping;
    private readonly Dictionary<SdfGridData, uint> _grids = [];
    private readonly object _gridGate = new();
    private int _samples;
    private bool _initialized;

    public DeviceSdfFields(IGraphicsBackend backend, Device device, DeviceSdfCapacity capacity)
    {
        _backend = backend; _device = device; Capacity = capacity;
        Fields = backend.CreateBuffer(device, new((ulong)capacity.Fields * 80,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "SDF field metadata"), MemoryType.DeviceLocal);
        _fieldView = backend.CreateBufferUav(device, new(Fields, new(0, Fields.Info.Size), StructureStride: 80));
        _fieldUpload = backend.CreateBuffer(device, new(Fields.Info.Size, BufferUsages.CopySource, "SDF metadata upload"), MemoryType.Upload);
        _fieldMapping = backend.Map(_fieldUpload, MapType.Write, BufferRange.Whole);
        if (capacity.Samples > 0)
        {
            Samples = backend.CreateBuffer(device, new((ulong)capacity.Samples * 4,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "SDF samples"), MemoryType.DeviceLocal);
            _sampleView = backend.CreateBufferUav(device, new(Samples, new(0, Samples.Info.Size), StructureStride: 4));
            _sampleUpload = backend.CreateBuffer(device, new(Samples.Info.Size, BufferUsages.CopySource, "SDF sample upload"), MemoryType.Upload);
            _sampleMapping = backend.Map(_sampleUpload, MapType.Write, BufferRange.Whole);
        }
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "SDF field transfer"));
    }

    public DeviceSdfCapacity Capacity { get; }
    public Buffer Fields { get; }
    public Buffer? Samples { get; }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    public UploadDeviceSdfInputs UploadInputs() => new(this);
    public IReadOnlyDictionary<string, ResourceBinding[]> GetBindings()
    {
        var bindings = new Dictionary<string, ResourceBinding[]>(StringComparer.Ordinal)
        { ["DeviceSdfInputs"] = [ResourceBinding.WritableBuffer(_fieldView)] };
        if (_sampleView is not null) bindings.Add("DeviceSdfSamples", [ResourceBinding.WritableBuffer(_sampleView)]);
        return bindings;
    }
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler)
    {
        var accesses = new List<JobResourceAccess> { JobResourceAccess.Read(scheduler.GetResourceToken(Fields), 0, (long)Fields.Info.Size) };
        if (Samples is not null) accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(Samples), 0, (long)Samples.Info.Size));
        return accesses.ToArray();
    }
    public uint WriteGridSamples(SdfGridData data)
    {
        lock (_gridGate)
        {
            if (!_grids.TryGetValue(data, out uint first))
            {
                first = (uint)_samples;
                _grids.Add(data, first);
                _samples += data.Distances.Length;
            }
            MemoryMarshal.AsBytes(data.Distances.Span).CopyTo(_sampleMapping.Bytes[(int)(first * 4)..]);
            return first;
        }
    }
    internal void WriteField(int index, in DeviceSdfInput input) =>
        MemoryMarshal.Write(_fieldMapping.Bytes.Slice(index * 80, 80), in input);
    internal void CollectUploadWrites(int index, ref JobResourceAccessBuilder resources)
    {
        resources.Write(_fieldUpload, (long)index * 80, 80);
        if (_sampleUpload is not null) resources.Write(_sampleUpload, 0, (long)_sampleUpload.Info.Size);
    }
    internal JobHandle ScheduleUpload(Scheduler scheduler, JobHandle dependency)
    {
        var accesses = new List<JobResourceAccess>();
        var resources = new JobResourceAccessBuilder(scheduler, accesses);
        resources.Write(Fields, 0, (long)Fields.Info.Size);
        resources.Read(_fieldUpload, 0, (long)_fieldUpload.Info.Size);
        if (Samples is not null)
        {
            resources.Write(Samples, 0, (long)Samples.Info.Size);
            resources.Read(_sampleUpload!, 0, (long)_sampleUpload!.Info.Size);
        }
        resources.Write(_commands);
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses.ToArray(), dependency);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _backend.Begin(_commands, new(0, 0, 16));
        Upload(Fields, _fieldUpload, ref _fieldMapping);
        if (Samples is not null) Upload(Samples, _sampleUpload!, ref _sampleMapping);
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void Upload(Buffer target, Buffer source, ref MappedBuffer mapping)
    {
        mapping.Flush(mapping.Range);
        _backend.Barrier(_commands, new BufferBarrier(target, _initialized ? PipelineSync.ComputeShading : target.InitialSync,
            PipelineSync.Copy, _initialized ? ResourceAccess.UnorderedAccess : target.InitialAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(source, 0, target, 0, target.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(target, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }
    public void Dispose()
    {
        _fieldMapping.Dispose();
        if (Samples is not null) _sampleMapping.Dispose();
        _commands.Dispose(); _sampleView?.Dispose(); _fieldView.Dispose();
        _sampleUpload?.Dispose(); _fieldUpload.Dispose(); Samples?.Dispose(); Fields.Dispose();
    }
}

public readonly struct UploadDeviceSdfInputs(DeviceSdfFields fields) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        fields.ScheduleUpload(WorldJobs.GetScheduler(context.World), dependency);
}
