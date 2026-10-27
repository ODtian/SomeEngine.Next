using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Graphics;
using SomeEngine.Physics;

namespace DefaultGame;

internal interface ISceneBodyGeometry
{
    float Add(World world, Entity body, SceneBodyData input, int index);
}

internal interface ISceneDeviceGeometry<TInputs> : ISceneBodyGeometry
    where TInputs : class, IPhysicsStage, IDisposable
{
    DeviceGeometryCapacity GetCapacity(int bodies);
    ComputePipelineDesc[] Detection(PhysicsSceneShaders shaders);
    TInputs CreateInputs(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionGeometry geometry, PhysicsSceneShaders shaders,
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, PhysicsSceneSettings settings,
        float contactDistance, int rowsPerPacket);
}

internal readonly struct SceneBoxGeometry : ISceneDeviceGeometry<DeviceSceneGeometryInputs>
{
    public float Add(World world, Entity body, SceneBodyData input, int index)
    {
        Vector3 half = new((float)input.Size[0] * .5f, (float)input.Size[1] * .5f, (float)input.Size[2] * .5f);
        world.Add(body, new BoxGeometry(half));
        return half.Length();
    }

    public DeviceGeometryCapacity GetCapacity(int bodies) => new(bodies, 0, 0, 0);
    public ComputePipelineDesc[] Detection(PhysicsSceneShaders shaders) => [shaders.Detection()];
    public DeviceSceneGeometryInputs CreateInputs(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionGeometry geometry, PhysicsSceneShaders shaders,
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, PhysicsSceneSettings settings,
        float contactDistance, int rowsPerPacket) =>
        new(backend, device, grid, geometry, shaders.Geometry(contactDistance), pairs,
            settings.GeometryInputsFixed, settings.Timings, rowsPerPacket);
}
