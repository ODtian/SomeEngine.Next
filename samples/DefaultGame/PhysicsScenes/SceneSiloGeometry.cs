using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Graphics;
using SomeEngine.Physics;

namespace DefaultGame;

internal readonly struct SceneSiloGeometry : ISceneDeviceGeometry<DeviceSiloGeometryInputs>
{
    public float Add(World world, Entity body, SceneBodyData input, int index)
    {
        if (index < PhysicsSiloScenes.StaticBodies) return default(SceneBoxGeometry).Add(world, body, input, index);
        switch ((index - PhysicsSiloScenes.StaticBodies) % 3)
        {
            case 0: world.Add(body, new SphereGeometry(PhysicsSiloScenes.Radius)); return PhysicsSiloScenes.Radius;
            case 1: return default(SceneBoxGeometry).Add(world, body, input, index);
            default: world.Add(body, new ConvexHullGeometry(PhysicsSiloScenes.Hull, 0)); return PhysicsSiloScenes.Radius;
        }
    }

    public DeviceGeometryCapacity GetCapacity(int bodies) => new(bodies, PhysicsSiloScenes.Hull.Vertices.Length,
        PhysicsSiloScenes.Hull.Faces.Length, PhysicsSiloScenes.Hull.FaceVertices.Length);
    public ComputePipelineDesc[] Detection(PhysicsSceneShaders shaders) => shaders.SiloDetection();
    public DeviceSiloGeometryInputs CreateInputs(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionGeometry geometry, PhysicsSceneShaders shaders,
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, PhysicsSceneSettings settings,
        float contactDistance, int rowsPerPacket) =>
        new(backend, device, grid, geometry, shaders, pairs, settings, contactDistance, rowsPerPacket);
}
