using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneGeometryInputs : IPhysicsStage, IDisposable
{
    private readonly DeviceCollisionGrid _grid;
    private readonly WriteDeviceGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>> _references;
    private readonly UpdateDeviceGeometry _upload;
    private readonly UpdateDeviceGeometry? _resident;
    private readonly bool _inputsFixed;
    private readonly PhysicsSceneTimings? _timings;
    private bool _uploaded;

    internal DeviceSceneGeometryInputs(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionGeometry geometry, ComputePipelineDesc kernel,
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs,
        bool inputsFixed, PhysicsSceneTimings? timings, int rowsPerPacket)
    {
        _grid = grid; _inputsFixed = inputsFixed; _timings = timings;
        _references = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(
            pairs, default, 0, rowsPerPacket);
        _upload = new(backend, device, grid, geometry, kernel, 0, DeviceInputTransfer.Upload);
        if (inputsFixed) _resident = new(backend, device, grid, geometry, kernel, 0, DeviceInputTransfer.Resident);
    }

    internal UpdateDeviceSceneGeometry<DeviceSceneGeometryInputs> Update() => new(this);

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (_inputsFixed && _uploaded)
            return _resident!.Update().Measure(_timings, "Dynamic geometry and bounds").Schedule(in context, dependency);
        _uploaded = true;
        dependency = _grid.BeginInputs().Then(_references).Measure(_timings, "Geometry references")
            .Schedule(in context, dependency);
        return _upload.Update().Measure(_timings, "Dynamic geometry and bounds").Schedule(in context, dependency);
    }

    public void Dispose() { _resident?.Dispose(); _upload.Dispose(); _references.Dispose(); }
}

internal readonly struct UpdateDeviceSceneGeometry<TInputs>(TInputs inputs) : IPhysicsStage
    where TInputs : class, IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}
