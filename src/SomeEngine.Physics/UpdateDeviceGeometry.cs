using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Reads current device body states and updates one concrete geometry type and its candidate bounds.</summary>
public sealed class UpdateDeviceGeometry : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceCollisionGeometry _geometry;
    private readonly DeviceInputTransfer _transfer;
    private readonly uint _geometryType;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[] _bindings;
    private readonly Buffer _parameters;
    private readonly BufferCbv _parameterView;
    private readonly BufferUav _boundsView;
    private readonly CommandContext _commands;

    public UpdateDeviceGeometry(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionGeometry geometry, ComputePipelineDesc kernel, uint geometryType, DeviceInputTransfer transfer,
        IReadOnlyDictionary<string, ResourceBinding[]>? extraBindings = null, ReadOnlyMemory<JobResourceAccess> extraAccesses = default)
    {
        _backend = backend; _device = device; _grid = grid; _geometry = geometry; _transfer = transfer; _geometryType = geometryType;
        _extraAccesses = extraAccesses.ToArray();
        _parameters = backend.CreateBuffer(device, new(256, BufferUsages.Constant, "Geometry update parameters"), MemoryType.Upload);
        _parameterView = backend.CreateBufferCbv(device, new(_parameters, new(0, 256)));
        _boundsView = backend.CreateBufferUav(device, new(grid.Bodies, new(0, grid.Bodies.Info.Size), StructureStride: DeviceCollisionGrid.InputStride));
        var bindings = new Dictionary<string, ResourceBinding[]>(StringComparer.Ordinal)
        {
            ["GeometryUpdateParameters"] = [ResourceBinding.ConstantBuffer(_parameterView)],
            ["GeometryCollisionInputs"] = [ResourceBinding.WritableBuffer(_boundsView)],
        };
        geometry.AddBindings(bindings);
        if (extraBindings is not null) foreach (var binding in extraBindings) bindings.Add(binding.Key, binding.Value);
        _pipeline = backend.CreateComputePipeline(device, kernel);
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _bindings = DeviceCollisionGrid.OrderBindings(_layout, bindings);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Current device geometry"));
    }

    private readonly JobResourceAccess[] _extraAccesses;
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    public UpdateDeviceGeometryStage Update() => new(this);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var accesses = new List<JobResourceAccess>();
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var resources = new JobResourceAccessBuilder(scheduler, accesses);
        _geometry.CollectAccesses(_transfer, ref resources);
        resources.Write(_geometry.Inputs, 0, (long)_geometry.Inputs.Info.Size);
        resources.Write(_geometry.Points!, 0, (long)_geometry.Points!.Info.Size);
        resources.Write(_grid.Bodies, 0, (long)_grid.Bodies.Info.Size);
        if (_transfer == DeviceInputTransfer.Upload) resources.Read(_grid.Upload, 0, (long)_grid.Upload.Info.Size);
        resources.Write(_parameters, 0, 256);
        resources.Write(_commands);
        accesses.AddRange(_extraAccesses);
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses.ToArray(), dependency);
    }

    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        var parameters = new GeometryUpdateParameters((uint)_geometry.Capacity.Bodies, _geometryType);
        using (MappedBuffer mapping = _backend.Map(_parameters, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.Write(mapping.Bytes, in parameters); mapping.Flush(new(0, 8));
        }
        _backend.Begin(_commands, new(32, 0, 32));
        _geometry.UploadInputs(_commands, _transfer);
        _grid.UploadInputs(_commands, _transfer);
        _backend.SetPipeline(_commands, _pipeline);
        _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings, []));
        _backend.Dispatch(_commands, new((uint)(_geometry.Capacity.Bodies + 63) / 64, 1, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly record struct GeometryUpdateParameters(uint Count, uint GeometryType);
    public void Dispose()
    {
        _commands.Dispose(); _pipeline.Dispose(); _boundsView.Dispose(); _parameterView.Dispose(); _parameters.Dispose();
    }
}

public readonly struct UpdateDeviceGeometryStage(UpdateDeviceGeometry update) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => update.Schedule(in context, dependency);
}
