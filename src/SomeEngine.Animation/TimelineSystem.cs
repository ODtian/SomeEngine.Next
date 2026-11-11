using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Runs before evaluators: advances enabled timelines and clears their running bits.</summary>
public struct TimelineSystem(Entity time = default) : ISystem<ImmediateSystemContext>
{
    private readonly Entity _time = time;
    private Query _timelines;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _timelines = context.World.Query(new QueryDescription().WithStored<Timeline>().With<Timeline>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        AnimationTime time = _time == Entity.Null
            ? new AnimationTime((float)context.DeltaTime)
            : context.World.ReadComponentData<AnimationTime>(_time);
        DevelopmentCheck.Require(float.IsFinite(time.Delta) && time.Delta >= 0, "Animation frame time must be finite and nonnegative.");
        DevelopmentCheck.Require(float.IsFinite(time.GameScale) && time.GameScale >= 0, "Game time scale must be finite and nonnegative.");
        AnimationDeltas deltas = time.Deltas;
        {
            using QueryScope read = _timelines.Since(context.LastSystemVersion).Open();
            Visit(in read, ref deltas);

            static void Visit(scoped in QueryScope rows, ref AnimationDeltas deltas)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var values0 = chunk.Has<Timeline>() ? chunk.Write<Timeline>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        ref Timeline timeline = ref values0[rowIndex];
                        timeline.Running = 0;
                        timeline.Delta = timeline.Paused ? 0 : deltas.For(timeline.Clock) * timeline.Speed;
                        timeline.Time += timeline.Delta;

                    }
                }

            }
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
