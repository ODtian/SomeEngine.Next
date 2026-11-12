using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Starts the frame's result contributions before concrete node evaluation.</summary>
public struct PoseFrameSystem : ISystem<ImmediateSystemContext>
{
    private Query _results;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _results = context.World.Query(new QueryDescription().WithStored<Buffer<PoseResult>>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        using QueryScope rows = _results.Open();
        foreach (QueryChunk chunk in rows)
        {
            var buffers0 = chunk.Has<Buffer<PoseResult>>() ? chunk.GetBuffers<PoseResult>() : default;
            for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
            {
                foreach (ref PoseResult result in buffers0[rowIndex].AsSpan())
                {
                    result.Weight = 0;
                    result.RootMotion = RootMotionDelta.Identity;
                }
            }
        }

    }

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
