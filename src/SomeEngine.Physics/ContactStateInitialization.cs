using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Copies retained solver words and response words, or installs defaults for newly detected points.</summary>
public sealed class ContactStateInitialization<TState> : IDisposable where TState : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceContactMatches<TState> _matches;
    private readonly DeviceContactManifolds<TState> _contacts;
    private readonly DeviceContactColumns _first;
    private readonly List<IDisposable> _objects = [];
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[][][] _bindings;
    private readonly CommandContext _commands;

    public ContactStateInitialization(IGraphicsBackend backend, Device device, DeviceContactMatches<TState> matches,
        ComputePipelineDesc kernel)
    {
        _backend = backend; _device = device; _matches = matches; _contacts = matches.Contacts; _first = _contacts.Current;
        _pipeline = Keep(backend.CreateComputePipeline(device, kernel));
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Buffer settings = Keep(backend.CreateBuffer(device, new(768, BufferUsages.Constant), MemoryType.Upload));
        using (MappedBuffer mapping = backend.Map(settings, MapType.Write, BufferRange.Whole))
        {
            uint words = (uint)Unsafe.SizeOf<TState>() / 4, responseWords = 12, supportWords = 16;
            MemoryMarshal.Write(mapping.Bytes, in words); MemoryMarshal.Write(mapping.Bytes[256..], in responseWords); MemoryMarshal.Write(mapping.Bytes[512..], in supportWords);
            mapping.Flush(mapping.Range);
        }
        BufferCbv stateSettings = Keep(backend.CreateBufferCbv(device, new(settings, new(0, 256))));
        BufferCbv responseSettings = Keep(backend.CreateBufferCbv(device, new(settings, new(256, 256))));
        BufferCbv supportSettings = Keep(backend.CreateBufferCbv(device, new(settings, new(512, 256))));
        _bindings = [Bind(_contacts.Current, _contacts.Previous), Bind(_contacts.Previous, _contacts.Current)];
        _commands = Keep(backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact history transfer")));
        ResourceBinding[][] Bind(DeviceContactColumns current, DeviceContactColumns previous)
        {
            var bindings = new List<ResourceBinding[]>
            {
                Values(current,previous,current.States,previous.States,_contacts.DefaultWords,stateSettings),
                Values(current,previous,current.Responses,previous.Responses,_contacts.ResponseDefaultWords,responseSettings)
            };
            if (current.SupportStates is { } support)
                bindings.Add(Values(current, previous, support, previous.SupportStates!, _contacts.ResponseDefaultWords, supportSettings));
            return [.. bindings];
        }
        ResourceBinding[] Values(DeviceContactColumns current, DeviceContactColumns previous, Buffer values, Buffer prior,
            BufferSrv defaults, BufferCbv parameters)
        {
            BufferUav output = Keep(backend.CreateBufferUav(device, new(values, BufferRange.Whole, StructureStride: 4)));
            BufferSrv input = Keep(backend.CreateBufferSrv(device, new(prior, BufferRange.Whole, StructureStride: 4)));
            var bindings = new Dictionary<string, ResourceBinding[]>
            {
                ["ContactStateInitializationParameters"] = [ResourceBinding.ConstantBuffer(parameters)],
                ["ContactStateCounts"] = [ResourceBinding.ReadOnlyBuffer(current.CountValues.ReadView)],
                ["ContactStateMatches"] = [ResourceBinding.ReadOnlyBuffer(matches.MatchView)],
                ["ContactStateDefaults"] = [ResourceBinding.ReadOnlyBuffer(defaults)],
                ["PreviousContactStateValues"] = [ResourceBinding.ReadOnlyBuffer(input)],
                ["ContactStateValues"] = [ResourceBinding.WritableBuffer(output)]
            };
            return DeviceCollisionGrid.OrderBindings(_layout, bindings);
        }
    }
    public InitializeContactState<TState> Initialize() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        DeviceContactColumns current = _contacts.Current, previous = _contacts.Previous;
        JobResourceAccess[] accesses = [..current.GetWriteAccesses(scheduler),..previous.GetReadAccesses(scheduler),
            JobResourceAccess.Read(scheduler.GetResourceToken(_matches.Matches),0,(long)_matches.Matches.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_matches.Arguments),0,(long)_matches.Arguments.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, current, previous), accesses, dependency);
    }
    private QueueCompletion Submit(DeviceContactColumns current, DeviceContactColumns previous)
    {
        _backend.Begin(_commands, new(0, 0, 32));
        current.StateValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        current.ResponseValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        current.SupportValues?.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        current.CountValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
        _backend.Barrier(_commands, new BufferBarrier(_matches.Matches, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.ShaderResource));
        ResourceBinding[][] bindings = _bindings[ReferenceEquals(current, _first) ? 0 : 1];
        _backend.SetPipeline(_commands, _pipeline);
        foreach (ResourceBinding[] values in bindings)
        {
            _backend.SetTransientParameterBindings(_commands, new(_layout, values, []));
            _matches.DispatchPoints(_commands);
        }
        _backend.Barrier(_commands, new BufferBarrier(_matches.Matches, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.ShaderResource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private T Keep<T>(T value) where T : IDisposable { _objects.Add(value); return value; }
    public void Dispose() { for (int index = _objects.Count - 1; index >= 0; index--) _objects[index].Dispose(); }
    private readonly struct Submission(ContactStateInitialization<TState> input, DeviceContactColumns current,
        DeviceContactColumns previous) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => input._backend;
        public Device Device => input._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => input.Submit(current, previous);
    }
}
public readonly struct InitializeContactState<TState>(ContactStateInitialization<TState> input) : IDevicePhysicsStage where TState : unmanaged
{
    public object InputKey => input;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => input.Schedule(in context, dependency);
}
