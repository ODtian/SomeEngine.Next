using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSiloGeometryInputs : IPhysicsStage, IDisposable
{
    private readonly DeviceCollisionGrid _grid;
    private readonly WriteDeviceGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>> _spheres;
    private readonly WriteDeviceGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>> _boxes;
    private readonly WriteDeviceGeometryReferences<ConvexHullGeometry, DeviceComponentSource<ConvexHullGeometry, DeviceHullReader>> _hulls;
    private readonly UpdateDeviceGeometry _upload, _spheresUpdate, _boxesUpdate, _hullsUpdate;
    private readonly PhysicsSceneSettings _settings;
    private bool _uploaded;

    internal DeviceSiloGeometryInputs(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionGeometry geometry, PhysicsSceneShaders shaders,
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, PhysicsSceneSettings settings,
        float contactDistance, int rowsPerPacket)
    {
        _grid = grid; _settings = settings;
        _spheres = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 0, rowsPerPacket);
        _boxes = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(pairs, default, 1, rowsPerPacket);
        _hulls = grid.WriteGeometryReferences<ConvexHullGeometry, DeviceComponentSource<ConvexHullGeometry, DeviceHullReader>>(pairs, default, 2, rowsPerPacket);
        ComputePipelineDesc sphere = shaders.SiloGeometry("DeviceSphereUpdate", contactDistance);
        _upload = new(backend, device, grid, geometry, sphere, 0, DeviceInputTransfer.Upload);
        _spheresUpdate = new(backend, device, grid, geometry, sphere, 0, DeviceInputTransfer.Resident);
        _boxesUpdate = new(backend, device, grid, geometry, shaders.SiloGeometry("DeviceBoxUpdate", contactDistance), 1, DeviceInputTransfer.Resident);
        _hullsUpdate = new(backend, device, grid, geometry, shaders.SiloGeometry("DeviceHullUpdate", contactDistance), 2, DeviceInputTransfer.Resident);
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (_settings.GeometryInputsFixed && _uploaded)
            dependency = _spheresUpdate.Update().Measure(_settings.Timings, "Sphere geometry and bounds").Schedule(in context, dependency);
        else
        {
            _uploaded = true;
            dependency = _grid.BeginInputs().Then(_spheres).Then(_boxes).Then(_hulls)
                .Measure(_settings.Timings, "Geometry references").Schedule(in context, dependency);
            dependency = _upload.Update().Measure(_settings.Timings, "Sphere geometry and bounds").Schedule(in context, dependency);
        }
        dependency = _boxesUpdate.Update().Measure(_settings.Timings, "Box geometry and bounds").Schedule(in context, dependency);
        return _hullsUpdate.Update().Measure(_settings.Timings, "Hull geometry and bounds").Schedule(in context, dependency);
    }

    public void Dispose()
    {
        _hullsUpdate.Dispose(); _boxesUpdate.Dispose(); _spheresUpdate.Dispose(); _upload.Dispose();
        _hulls.Dispose(); _boxes.Dispose(); _spheres.Dispose();
    }
}
