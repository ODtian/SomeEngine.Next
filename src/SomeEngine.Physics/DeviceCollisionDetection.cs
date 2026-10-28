using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Concrete device kernels consuming consecutive resident type bins into one geometric result range.</summary>
public sealed partial class DeviceCollisionDetection : IDisposable
{
    public const int ContactStride = 104;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceCollisionGeometry _geometry;
    private readonly int _firstBin;
    private readonly ConvexCollisionSettings _settings;
    private readonly float _contactDistance;
    private readonly DeviceInputTransfer _transfer;
    private readonly DeviceCollisionReadback _readback;
    private readonly List<IDisposable> _objects = [];
    private readonly List<Buffer> _workBuffers = [];
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly JobResourceAccess[] _extraAccesses;
    private readonly Buffer _parameters;
    private readonly Buffer? _readbackBuffer;
    private readonly CommandContext _commands;
    private readonly DeviceCollisionDistance[] _distances;
    private readonly CollisionContact[] _contacts;
    private readonly int[] _contactCounts;
    private readonly int[] _binBounds = new int[2];
    private bool _initialized;

    public DeviceCollisionDetection(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionGeometry geometry, ComputePipelineDesc kernel, int bin, DeviceDetectionCapacity capacity,
        ConvexCollisionSettings settings, float contactDistance, DeviceInputTransfer transfer,
        DeviceCollisionReadback readback, IReadOnlyDictionary<string, ResourceBinding[]>? extraBindings = null,
        ReadOnlyMemory<JobResourceAccess> extraAccesses = default)
        : this(backend, device, grid, geometry, [kernel], bin, capacity, settings, contactDistance, transfer, readback,
            extraBindings, extraAccesses) { }

    public DeviceCollisionDetection(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionGeometry geometry, ReadOnlySpan<ComputePipelineDesc> kernels, int firstBin, DeviceDetectionCapacity capacity,
        ConvexCollisionSettings settings, float contactDistance, DeviceInputTransfer transfer,
        DeviceCollisionReadback readback, IReadOnlyDictionary<string, ResourceBinding[]>? extraBindings = null,
        ReadOnlyMemory<JobResourceAccess> extraAccesses = default)
    {
        _backend = backend;
        _device = device;
        _grid = grid;
        _geometry = geometry;
        _firstBin = firstBin;
        Capacity = capacity;
        _settings = settings;
        _contactDistance = contactDistance;
        _transfer = transfer;
        _readback = readback;
        _extraAccesses = extraAccesses.ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>(StringComparer.Ordinal);
        Distances = CreateWorkBuffer("CollisionDistances", (ulong)capacity.Pairs * 64, 64, bindings);
        Contacts = CreateWorkBuffer("CollisionContacts", (ulong)capacity.Pairs * 4 * ContactStride, ContactStride, bindings);
        ContactCounts = CreateWorkBuffer("CollisionContactCounts", (ulong)capacity.Pairs * 4, 4, bindings);
        CreateContactBodies(bindings);
        ulong vertices = (ulong)(4 + settings.PenetrationIterations);
        CreateWorkBuffer("PenetrationVertices", (ulong)capacity.WorkItems * vertices * 32, 32, bindings);
        CreateWorkBuffer("PenetrationFaces", (ulong)capacity.WorkItems * vertices * 2 * 28, 28, bindings);
        CreateWorkBuffer("PenetrationEdges", (ulong)capacity.WorkItems * vertices * 6 * 8, 8, bindings);
        CreateWorkBuffer("CollisionFaces", (ulong)capacity.WorkItems * 2 * (ulong)capacity.FaceVertices * 16, 16, bindings);
        ulong polygon = (ulong)(2 * capacity.FaceVertices + 4);
        CreateWorkBuffer("ContactPolygon", (ulong)capacity.WorkItems * polygon * 16, 16, bindings);
        CreateWorkBuffer("ContactClip", (ulong)capacity.WorkItems * polygon * 16, 16, bindings);
        CreateWorkBuffer("ManifoldCandidates", (ulong)capacity.WorkItems * polygon * 48, 48, bindings);
        BufferUav pairsView = Keep(backend.CreateBufferUav(device,
            new(grid.Pairs, new(0, grid.Pairs.Info.Size), StructureStride: DeviceCollisionGrid.PairStride)));
        BufferUav boundsView = Keep(backend.CreateBufferUav(device,
            new(grid.BinOffsets, new(0, grid.BinOffsets.Info.Size), StructureStride: 4)));
        _parameters = Keep(backend.CreateBuffer(device, new((ulong)kernels.Length * 256, BufferUsages.Constant,
            "Device collision parameters"), MemoryType.Upload));
        _readbackBuffer = readback == DeviceCollisionReadback.None ? null :
            Keep(backend.CreateBuffer(device, new(ResultReadbackSize,
                BufferUsages.CopyDestination, "Device collision result readback"), MemoryType.Readback));
        bindings.Add("DetectionParameters", []);
        bindings.Add("DetectionPairs", [ResourceBinding.WritableBuffer(pairsView)]);
        bindings.Add("DetectionBinOffsets", [ResourceBinding.WritableBuffer(boundsView)]);
        geometry.AddBindings(bindings);
        if (extraBindings is not null)
            foreach (var binding in extraBindings) bindings.Add(binding.Key, binding.Value);
        _pipelines = new Pipeline[kernels.Length];
        _layouts = new VariableLayoutReflection[kernels.Length];
        _bindings = new ResourceBinding[kernels.Length][];
        for (int index = 0; index < kernels.Length; index++)
        {
            BufferCbv parametersView = Keep(backend.CreateBufferCbv(device, new(_parameters, new((ulong)index * 256, 256))));
            bindings["DetectionParameters"] = [ResourceBinding.ConstantBuffer(parametersView)];
            _pipelines[index] = Keep(backend.CreateComputePipeline(device, kernels[index]));
            _layouts[index] = kernels[index].Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            _bindings[index] = DeviceCollisionGrid.OrderBindings(_layouts[index], bindings);
        }
        _commands = Keep(backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1, Label: "Device collision detection")));
        _distances = (readback & DeviceCollisionReadback.Distances) != 0 ? new DeviceCollisionDistance[capacity.Pairs] : [];
        _contacts = (readback & DeviceCollisionReadback.Contacts) != 0 ? new CollisionContact[capacity.Pairs * 4] : [];
        _contactCounts = (readback & DeviceCollisionReadback.Contacts) != 0 ? new int[capacity.Pairs] : [];
    }

    public DeviceDetectionCapacity Capacity { get; }
    public Buffer Distances { get; }
    public Buffer Contacts { get; }
    public Buffer ContactCounts { get; }
    public QueueCompletion Completion { get; private set; }
    public long Number { get; private set; }
    public double Time { get; private set; }
    public int Count => _binBounds[1] - _binBounds[0];
    public ReadOnlySpan<DeviceCollisionDistance> ReadDistances() => _distances.AsSpan(0, Count);
    public ReadOnlySpan<CollisionContact> ReadContacts(int pair) => _contacts.AsSpan(pair * 4, _contactCounts[pair]);
    public DetectDeviceCollision Detect() => new(this);
    private ulong CountReadbackOffset => 8 + ((_readback & DeviceCollisionReadback.Distances) != 0 ? Distances.Info.Size : 0);
    private ulong ContactReadbackOffset => CountReadbackOffset +
        ((_readback & DeviceCollisionReadback.Contacts) != 0 ? ContactCounts.Info.Size : 0);

    public JobResourceAccess[] GetReadbackAccesses(Scheduler scheduler)
    {
        if (_readback == DeviceCollisionReadback.None) return [];
        var accesses = new List<JobResourceAccess> { JobResourceAccess.Read(scheduler, _binBounds) };
        if ((_readback & DeviceCollisionReadback.Distances) != 0)
            accesses.Add(JobResourceAccess.Read(scheduler, _distances));
        if ((_readback & DeviceCollisionReadback.Contacts) != 0)
        {
            accesses.Add(JobResourceAccess.Read(scheduler, _contacts));
            accesses.Add(JobResourceAccess.Read(scheduler, _contactCounts));
            if (_geometry.Bodies is not null)
            {
                accesses.Add(JobResourceAccess.Read(scheduler, _contactBodies));
                accesses.Add(JobResourceAccess.Read(scheduler, _contactBodyCounts));
            }
        }
        return [.. accesses];
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        WorldJobs.TryGetScheduler(context.World, out Scheduler? scheduler);
        var accesses = new List<JobResourceAccess>();
        var resources = new JobResourceAccessBuilder(scheduler!, accesses);
        resources.Read(_grid.Pairs, 0, (long)_grid.Pairs.Info.Size);
        resources.Read(_grid.BinOffsets, _firstBin * 4L, (_pipelines.Length + 1) * 4L);
        _geometry.CollectAccesses(_transfer, ref resources);
        foreach (Buffer buffer in _workBuffers) resources.Write(buffer, 0, (long)buffer.Info.Size);
        resources.Write(_parameters, 0, (long)_parameters.Info.Size);
        resources.Write(_commands);
        if (_readback != DeviceCollisionReadback.None)
            resources.Write(_readbackBuffer!, 0, (long)_readbackBuffer!.Info.Size);
        accesses.AddRange(_extraAccesses);
        JobResourceAccess[] deviceAccesses = [.. accesses];
        return scheduler!.Schedule(new SubmitDetection(this, scheduler, context, deviceAccesses), deviceAccesses, dependency);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DetectionParameters(uint Bin, uint PairCapacity, float Tolerance,
        uint DistanceIterations, uint PenetrationIterations, uint FaceCapacity, float ContactDistance, uint WorkItems,
        uint BodiesPerContact, uint FirstBin, double StartTime);

    private QueueCompletion Submit(in PhysicsStepContext context)
    {
        using (MappedBuffer mapping = _backend.Map(_parameters, MapType.Write, BufferRange.Whole))
        {
            for (int index = 0; index < _pipelines.Length; index++)
            {
                var parameters = new DetectionParameters((uint)(_firstBin + index), (uint)Capacity.Pairs, _settings.Tolerance,
                    (uint)_settings.DistanceIterations, (uint)_settings.PenetrationIterations,
                    (uint)Capacity.FaceVertices, _contactDistance, (uint)Capacity.WorkItems,
                    (uint)Capacity.BodiesPerContact, (uint)_firstBin, context.StartTime);
                MemoryMarshal.Write(mapping.Bytes[(index * 256)..], in parameters);
            }
            mapping.Flush(mapping.Range);
        }
        uint descriptorCapacity = 64u * (uint)_pipelines.Length;
        _backend.Begin(_commands, new CommandRecordingDesc(descriptorCapacity, 0, descriptorCapacity));
        _geometry.UploadInputs(_commands, _transfer);
        if (!_initialized)
            foreach (Buffer buffer in _workBuffers)
                _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        for (int index = 0; index < _pipelines.Length; index++)
        {
            if (index != 0)
                foreach (Buffer buffer in _workBuffers)
                    _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                        ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
            _backend.SetPipeline(_commands, _pipelines[index]);
            _backend.SetTransientParameterBindings(_commands, new(_layouts[index], _bindings[index], []));
            _backend.Dispatch(_commands, new((uint)((Capacity.WorkItems + 63) / 64), 1, 1));
        }
        if (_readback != DeviceCollisionReadback.None)
        {
            CopyResults(_grid.BinOffsets, (ulong)_firstBin * 4, 0, 4);
            CopyResults(_grid.BinOffsets, (ulong)(_firstBin + _pipelines.Length) * 4, 4, 4);
        }
        using RecordedCommands commands = _backend.End(_commands);
        Completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [commands], [], []));
        _initialized = true;
        return Completion;
    }

    private void CopyResults(Buffer source, ulong sourceOffset, ulong resultOffset, ulong size)
    {
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new BufferCopy(source, sourceOffset, _readbackBuffer!, resultOffset, size));
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
    }

    private readonly struct SubmitDetection(DeviceCollisionDetection detection, Scheduler scheduler,
        PhysicsStepContext context, JobResourceAccess[] deviceAccesses) : IJob
    {
        public void Execute()
        {
            QueueCompletion completion = detection.Submit(in context);
            JobHandle ready = scheduler.CreateExternalFenceHandle(
                new DeviceQueueFence(scheduler, detection._backend, completion), deviceAccesses);
            var accesses = new List<JobResourceAccess>();
            var resources = new JobResourceAccessBuilder(scheduler, accesses);
            if (detection._readback != DeviceCollisionReadback.None)
            {
                resources.Read(detection._readbackBuffer!, 0, (long)detection._readbackBuffer!.Info.Size);
                resources.Write(detection._binBounds.AsMemory());
            }
            if ((detection._readback & DeviceCollisionReadback.Distances) != 0)
                resources.Write(detection._distances.AsMemory());
            if ((detection._readback & DeviceCollisionReadback.Contacts) != 0)
            {
                resources.Write(detection._contacts.AsMemory());
                resources.Write(detection._contactCounts.AsMemory());
                if (detection._geometry.Bodies is not null)
                {
                    resources.Write(detection._contactBodies.AsMemory());
                    resources.Write(detection._contactBodyCounts.AsMemory());
                }
            }
            JobResourceAccess[] accepted = [.. accesses];
            JobHandle consumed;
            if (detection._readback == DeviceCollisionReadback.None)
                consumed = detection.ScheduleAccept(scheduler, in context, accepted, ready);
            else
            {
                JobResourceAccess[] bounds = [.. deviceAccesses, JobResourceAccess.Write(scheduler, detection._binBounds)];
                consumed = scheduler.Schedule(new ReadDetectionBounds(detection, scheduler, context, deviceAccesses, accepted), bounds, ready);
            }
            scheduler.ScheduleFinally(new CollectDetection(detection), default, consumed);
        }
    }

    private readonly struct AcceptDetection(DeviceCollisionDetection detection, PhysicsStepContext context) : IJob
    {
        public void Execute() => detection.Accept(in context);
    }

    private readonly struct CollectDetection(DeviceCollisionDetection detection) : IJob
    {
        public void Execute() => detection._backend.CollectCompleted(detection._device);
    }

    private T Keep<T>(T value) where T : IDisposable
    {
        _objects.Add(value);
        return value;
    }

    private Buffer CreateWorkBuffer(string name, ulong bytes, uint stride, Dictionary<string, ResourceBinding[]> bindings)
    {
        Buffer buffer = Keep(_backend.CreateBuffer(_device, new(bytes,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, name), MemoryType.DeviceLocal));
        BufferUav view = Keep(_backend.CreateBufferUav(_device, new(buffer, new(0, bytes), StructureStride: stride)));
        bindings.Add(name, [ResourceBinding.WritableBuffer(view)]);
        _workBuffers.Add(buffer);
        return buffer;
    }

    public void Dispose()
    {
        for (int index = _objects.Count - 1; index >= 0; index--) _objects[index].Dispose();
    }
}
