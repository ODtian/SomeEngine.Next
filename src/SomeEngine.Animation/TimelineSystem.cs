using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Runs before evaluators: advances enabled timelines and clears their running bits.</summary>
public struct TimelineSystem(Entity time = default) : ISystem<ImmediateSystemContext>
{
    private readonly Entity _time = time;
    private QueryHandle _timelines;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _timelines = context.World.Query(context.World.QueryDefinition().ReadWrite<Timeline>().Enabled<Timeline>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        AnimationTime time = _time == Entity.Null
            ? new AnimationTime((float)context.DeltaTime)
            : context.World.Read<AnimationTime>(_time);
        DevelopmentCheck.Require(float.IsFinite(time.Delta) && time.Delta >= 0, "Animation frame time must be finite and nonnegative.");
        DevelopmentCheck.Require(float.IsFinite(time.GameScale) && time.GameScale >= 0, "Game time scale must be finite and nonnegative.");
        context.World.ExecuteQuery(_timelines, context.LastSystemVersion, ref time,
            static (QueryCursor rows, ref AnimationTime time) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    ref Timeline timeline = ref row.ReadWrite<Timeline>();
                    timeline.Running = 0;
                    if (!timeline.Paused)
                        timeline.Time += time.For(timeline.Clock) * timeline.Speed;
                }
            });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_timelines);
}
