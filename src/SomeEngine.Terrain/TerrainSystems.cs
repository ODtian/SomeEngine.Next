using SomeEngine.ECS.Systems;

namespace SomeEngine.Terrains;

public sealed class TerrainCommitSystem(TerrainScene scene) : ISystem<ImmediateSystemContext>
{
    public void OnCreate(ref ImmediateSystemContext context) => scene.Bind(context.World);
    public void OnUpdate(ref ImmediateSystemContext context) => scene.CommitChanges();
    public void OnDestroy(ref ImmediateSystemContext context) => scene.Unbind();
}

public sealed class TerrainLodSystem(TerrainScene scene) : ISystem<ImmediateSystemContext>
{
    public void OnUpdate(ref ImmediateSystemContext context) => scene.SynchronizePlacements();
}

public sealed class TerrainMeshSystem(TerrainScene scene) : ISystem<ImmediateSystemContext>
{
    public void OnUpdate(ref ImmediateSystemContext context) => scene.ScheduleBuilds();
}
