using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Initial values for newly allocated contact rows in one selected state representation.</summary>
public sealed class ContactStateInitialization<TState> : IDisposable, IDevicePhysicsSubmission where TState : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceContactMatches _matches;
    private readonly DeviceConstraintState<TState> _state;
    private readonly Buffer _defaults, _settings;
    private readonly BufferSrv _defaultsView;
    private readonly BufferUav _stateView;
    private readonly BufferCbv _settingsView;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[] _bindings;
    private readonly CommandContext _commands;
    private bool _initialized;

    public ContactStateInitialization(IGraphicsBackend backend, Device device, DeviceContactMatches matches,
        DeviceConstraintState<TState> state, in TState value, ComputePipelineDesc kernel)
    {
        _backend=backend; _device=device; _matches=matches; _state=state;
        _defaults=backend.CreateBuffer(device,new((ulong)Unsafe.SizeOf<TState>(),BufferUsages.ShaderRead),MemoryType.Upload);
        using(MappedBuffer mapping=backend.Map(_defaults,MapType.Write,BufferRange.Whole))
        {
            MemoryMarshal.Write(mapping.Bytes,in value);
            mapping.Flush(mapping.Range);
        }
        _defaultsView=backend.CreateBufferSrv(device,new(_defaults,BufferRange.Whole,StructureStride:4));
        _stateView=backend.CreateBufferUav(device,new(state.States,BufferRange.Whole,StructureStride:4));
        _settings=backend.CreateBuffer(device,new(256,BufferUsages.Constant),MemoryType.Upload);
        _settingsView=backend.CreateBufferCbv(device,new(_settings,BufferRange.Whole));
        var bindings=new Dictionary<string,ResourceBinding[]>
        {
            ["DeviceContactGeometryRows"]=[ResourceBinding.WritableBuffer(matches.RowView)],
            ["ContactStateDefaults"]=[ResourceBinding.ReadOnlyBuffer(_defaultsView)],
            ["ContactStateValues"]=[ResourceBinding.WritableBuffer(_stateView)],
            ["ContactStateInitializationParameters"]=[ResourceBinding.ConstantBuffer(_settingsView)],
        };
        _pipeline=backend.CreateComputePipeline(device,kernel);
        _layout=kernel.Reflection.GetGlobalParamsVarLayout()??VariableLayoutReflection.Null;
        _bindings=DeviceCollisionGrid.OrderBindings(_layout,bindings);
        _commands=backend.CreateCommandContext(device,new(QueueType.Compute,0,1,Label:"Contact state initialization"));
    }

    public InitializeContactState<TState> Initialize() => new(this);
    IGraphicsBackend IDevicePhysicsSubmission.Backend=>_backend;
    Device IDevicePhysicsSubmission.Device=>_device;
    SomeEngine.Graphics.Queue IDevicePhysicsSubmission.Queue=>_backend.GetQueue(_device,QueueType.Compute);
    internal JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)
    {
        Scheduler scheduler=WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses=[_matches.ReadAccess,_state.GetWriteAccess(scheduler),
            JobResourceAccess.Read(scheduler.GetResourceToken(_matches.RowBuffer),0,(long)_matches.RowBuffer.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_defaults),0,(long)_defaults.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_settings)),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler,this,accesses,dependency);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _state.Memory?.EnsureRows(1);
        using(MappedBuffer mapping=_backend.Map(_settings,MapType.Write,BufferRange.Whole))
        {
            Span<uint> values=MemoryMarshal.Cast<byte,uint>(mapping.Bytes);
            values[0]=(uint)_matches.InputCount; values[1]=(uint)Unsafe.SizeOf<TState>()/4;
            mapping.Flush(new(0,8));
        }
        _backend.Begin(_commands,new(0,0,8));
        if(!_initialized) _backend.Barrier(_commands,new BufferBarrier(_defaults,_defaults.InitialSync,
            PipelineSync.ComputeShading,_defaults.InitialAccess,ResourceAccess.ShaderResource));
        _state.BeginWrite(_backend,_commands,PipelineSync.ComputeShading,ResourceAccess.UnorderedAccess);
        _backend.SetPipeline(_commands,_pipeline);
        _backend.SetTransientParameterBindings(_commands,new(_layout,_bindings,[]));
        if(_matches.InputCount!=0) _backend.Dispatch(_commands,DevicePhysicsDispatch.CreateGroups((_matches.InputCount+63)/64));
        _backend.Barrier(_commands,new MemoryBarrier(PipelineSync.ComputeShading,PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded=_backend.End(_commands);
        _initialized=true;
        return _backend.Submit(_backend.GetQueue(_device,QueueType.Compute),new([],[],[recorded],[],[]));
    }
    public void Dispose()
    {
        _commands.Dispose(); _pipeline.Dispose(); _settingsView.Dispose(); _settings.Dispose();
        _stateView.Dispose(); _defaultsView.Dispose(); _defaults.Dispose();
    }
}

public readonly struct InitializeContactState<TState>(ContactStateInitialization<TState> input):IDevicePhysicsStage where TState:unmanaged
{
    public object InputKey=>input;
    public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)=>input.Schedule(in context,dependency);
}
