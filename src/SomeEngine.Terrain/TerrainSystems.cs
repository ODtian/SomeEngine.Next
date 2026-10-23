using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Terrains;

public sealed class TerrainCommitSystem(TerrainScene scene) : ISystem<ImmediateSystemContext>
{
    public void OnCreate(ref ImmediateSystemContext context) => scene.Bind(context.World);
    public void OnUpdate(ref ImmediateSystemContext context) => scene.CommitChanges();
    public void OnDestroy(ref ImmediateSystemContext context) => scene.Unbind();
}

public sealed class TerrainLodSystem(TerrainScene scene, Func<Vector3>? cameraPosition = null) : ISystem<ImmediateSystemContext>
{
    public void OnUpdate(ref ImmediateSystemContext context) => scene.SynchronizePlacements(cameraPosition?.Invoke() ?? Vector3.Zero);
}

public sealed class TerrainMeshSystem(TerrainScene scene) : ISystem<ImmediateSystemContext>
{
    public void OnUpdate(ref ImmediateSystemContext context) => scene.ScheduleBuilds();
}
