using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceContactMatchKernels(ComputePipelineDesc Clear, ComputePipelineDesc Index,
    ComputePipelineDesc Match, ComputePipelineDesc Claim, ComputePipelineDesc NewInputs, ComputePipelineDesc RemovedRows,
    ComputePipelineDesc Geometry, ComputePipelineDesc DispatchArguments);

/// <summary>Matches consecutive device contact columns and leaves history indices on the device.</summary>
public sealed class DeviceContactMatches<TState> : IDisposable where TState : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceCollisionDetection _detection;
    private readonly DeviceBodies _bodies;
    private readonly DeviceContactManifolds<TState> _contacts;
    private readonly int _bucketCount;
    private readonly List<IDisposable> _objects = [];
    private readonly List<Buffer> _workBuffers = [];
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][][] _bindings;
    private readonly DeviceContactColumns _first;
    private readonly Buffer _arguments;
    private readonly IndirectCommandLayout _dispatch;
    private readonly CommandContext _commands;
    internal Buffer Arguments => _arguments;
    internal readonly Buffer Matches;
    internal readonly BufferSrv MatchView;
    private bool _initialized;

    public DeviceContactMatches(IGraphicsBackend backend, Device device, DeviceContactManifolds<TState> contacts,
        DeviceCollisionDetection detection, DeviceBodies bodies, DeviceContactMatchKernels kernels,
        ContactRefreshSettings settings, ContactMaterial material, int bucketCount)
    {
        _backend = backend; _device = device; _contacts = contacts; _detection = detection; _bodies = bodies;
        _bucketCount = bucketCount; _first = contacts.Current;
        var bindings = new Dictionary<string, ResourceBinding[]>();
        Create("ContactMatchHeads", bucketCount, 4);
        Create("ContactMatchNext", contacts.Capacity.Points, 4);
        Matches = contacts.PreviousPoints; MatchView = contacts.MatchValues.ReadView;
        bindings.Add("ContactMatches", [ResourceBinding.WritableBuffer(contacts.MatchValues.WriteView)]);
        Create("ContactMatchClaims", contacts.Capacity.Points, 8);
        Create("ContactNewInputs", contacts.Capacity.Points + 1, 4);
        bindings.Add("ContactRemovedRows", [ResourceBinding.WritableBuffer(contacts.RemovedValues.WriteView)]);
        _arguments = contacts.DispatchValues.Buffer;
        bindings.Add("DeviceContactDispatchArguments", [ResourceBinding.WritableBuffer(contacts.DispatchValues.WriteView)]);
        AddRead("DeviceMatchInputPairs", detection.InputPairs, 32);
        AddRead("DeviceMatchInputMaterials", detection.InputMaterials, 32);
        AddRead("DeviceMatchInputBins", detection.InputBins, 4);
        AddRead("DeviceMatchInputContacts", detection.Contacts, DeviceCollisionDetection.ContactStride);
        AddRead("DeviceMatchInputTotal", detection.ContactTotal, 4);
        AddRead("DeviceMatchInputCounts", detection.ContactCounts, 4);
        AddRead("DeviceMatchInputOffsets", detection.ContactOffsets, 4);
        AddRead("DeviceMatchInputBodyCounts", detection.ContactBodyCounts!, 4);
        AddRead("DeviceMatchInputBodies", detection.ContactBodies!, 32);
        bindings.Add("DeviceBodyStates", [ResourceBinding.WritableBuffer(bodies.View)]);
        Buffer constants = Keep(backend.CreateBuffer(device, new(512, BufferUsages.Constant), MemoryType.Upload));
        using (MappedBuffer mapping = backend.Map(constants, MapType.Write, BufferRange.Whole))
        {
            var match = new MatchSettings((uint)contacts.Capacity.Points, (uint)bucketCount,
                settings.MatchDistance * settings.MatchDistance, settings.MinimumNormalDot);
            var geometry = new GeometrySettings((uint)detection.FirstBin, (uint)detection.BinCount,
                (uint)detection.Capacity.BodiesPerContact, settings.WarmStart ? 1u : 0u, material);
            MemoryMarshal.Write(mapping.Bytes, in match);
            MemoryMarshal.Write(mapping.Bytes[256..], in geometry);
            mapping.Flush(mapping.Range);
        }
        BufferCbv matchConstants = Keep(backend.CreateBufferCbv(device, new(constants, new(0, 256))));
        BufferCbv geometryConstants = Keep(backend.CreateBufferCbv(device, new(constants, new(256, 256))));
        bindings.Add("ContactMatchSettings", [ResourceBinding.ConstantBuffer(matchConstants)]);
        bindings.Add("ContactGeometrySettings", [ResourceBinding.ConstantBuffer(geometryConstants)]);
        ComputePipelineDesc[] descriptions = [kernels.Clear,kernels.Index,kernels.Match,kernels.Claim,
            kernels.NewInputs,kernels.RemovedRows,kernels.Geometry,kernels.DispatchArguments];
        _pipelines = descriptions.Select(description => Keep(backend.CreateComputePipeline(device, description))).ToArray();
        _layouts = descriptions.Select(description => description.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        _bindings = [Bind(contacts.Current, contacts.Previous), Bind(contacts.Previous, contacts.Current)];
        _commands = Keep(backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Resident contact matching")));
        _dispatch = contacts.DispatchLayout;

        ResourceBinding[][] Bind(DeviceContactColumns current, DeviceContactColumns previous)
        {
            var all = new Dictionary<string, ResourceBinding[]>(bindings);
            current.AddBindings(all); previous.AddBindings(all, "PreviousContact", false);
            return _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, all)).ToArray();
        }
        Buffer Create(string name, int count, uint stride, BufferUsages usages = 0)
        {
            Buffer buffer = Keep(backend.CreateBuffer(device, new((ulong)count * stride,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | usages, name)));
            BufferUav view = Keep(backend.CreateBufferUav(device, new(buffer, BufferRange.Whole, StructureStride: stride)));
            bindings.Add(name, [ResourceBinding.WritableBuffer(view)]); _workBuffers.Add(buffer); return buffer;
        }
        void AddRead(string name, Buffer buffer, uint stride)
        {
            BufferSrv view = Keep(backend.CreateBufferSrv(device, new(buffer, BufferRange.Whole, StructureStride: stride)));
            bindings.Add(name, [ResourceBinding.ReadOnlyBuffer(view)]);
        }
    }

    public DeviceContactManifolds<TState> Contacts => _contacts;
    public MatchDeviceContacts<TState> Match() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        _contacts.Advance();
        DeviceContactColumns current = _contacts.Current, previous = _contacts.Previous;
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(current.GetWriteAccesses(scheduler));
        accesses.AddRange(previous.GetReadAccesses(scheduler));
        accesses.AddRange(_contacts.GetInitializationAccesses(scheduler));
        foreach (Buffer buffer in _workBuffers)
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size));
        foreach (Buffer buffer in new[]{_detection.InputPairs,_detection.InputMaterials,_detection.InputBins,_detection.Contacts,
            _detection.ContactTotal,_detection.ContactCounts,_detection.ContactOffsets,
            _detection.ContactBodies!,_detection.ContactBodyCounts!})
            accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size));
        accesses.Add(_bodies.GetReadAccess(scheduler));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(Matches), 0, (long)Matches.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_contacts.RemovedPoints), 0, (long)_contacts.RemovedPoints.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_arguments), 0, (long)_arguments.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, current, previous), [.. accesses], dependency);
    }

    private QueueCompletion Submit(DeviceContactColumns current, DeviceContactColumns previous)
    {
        _backend.Begin(_commands, new(0, 0, 256));
        _contacts.Initialize(_backend, _commands);
        current.BeginWrite(_backend, _commands); previous.BeginRead(_backend, _commands);
        _contacts.DispatchValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        _contacts.MatchValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        _contacts.RemovedValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        if (!_initialized)
            foreach (Buffer buffer in _workBuffers)
                _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        ResourceBinding[][] bindings = _bindings[ReferenceEquals(current, _first) ? 0 : 1];
        Bind(0); _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((Math.Max(_bucketCount, _contacts.Capacity.Points) + 63) / 64));
        Barrier(); Bind(7); _backend.Dispatch(_commands, new(1, 1, 1)); Barrier();
        _contacts.DispatchValues.Transition(_backend, _commands, PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
        Run(6, 0); Run(1, 2); Run(2, 0); Run(3, 1); Run(4, 1); Run(5, 2);
        _contacts.DispatchValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        void Bind(int index)
        {
            _backend.SetPipeline(_commands, _pipelines[index]);
            _backend.SetTransientParameterBindings(_commands, new(_layouts[index], bindings[index], []));
        }
        void Run(int index, int arguments)
        {
            Bind(index); _backend.ExecuteIndirect(_commands, _dispatch, new(_arguments, new((ulong)arguments * 12, 12)), 1); Barrier();
        }
    }
    internal void DispatchPoints(CommandContext commands) => _contacts.DispatchPoints(_backend, commands);
    private void Barrier() => _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
        ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    private T Keep<T>(T value) where T : IDisposable { _objects.Add(value); return value; }
    public void Dispose() { for (int index = _objects.Count - 1; index >= 0; index--) _objects[index].Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MatchSettings(uint Capacity, uint Buckets, float SquaredDistance, float NormalDot);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct GeometrySettings(uint FirstBin, uint BinCount, uint BodiesPerContact, uint Flags, ContactMaterial Material);
    private readonly struct Submission(DeviceContactMatches<TState> matches, DeviceContactColumns current,
        DeviceContactColumns previous) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => matches._backend;
        public Device Device => matches._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => matches.Submit(current, previous);
    }
}
public readonly struct MatchDeviceContacts<TState>(DeviceContactMatches<TState> matches) : IDevicePhysicsStage where TState : unmanaged
{
    public object InputKey => matches;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => matches.Schedule(in context, dependency);
}
