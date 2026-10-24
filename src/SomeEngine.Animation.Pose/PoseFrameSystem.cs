using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Starts the frame's result contributions before concrete node evaluation.</summary>
public struct PoseFrameSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _results;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _results = context.World.Query(context.World.QueryDefinition().WriteBuffer<PoseResult>());

    public void OnUpdate(ref ImmediateSystemContext context) =>
        context.World.ExecuteQuery(_results, static rows =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref PoseResult result in row.Buffer<PoseResult>().AsSpan())
                {
                    result.Weight = 0;
                    result.RootMotion = RootMotionDelta.Identity;
                }
        });

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_results);
}
