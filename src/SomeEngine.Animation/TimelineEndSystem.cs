using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Runs after evaluators: completes timelines with an empty running mask.</summary>
public struct TimelineEndSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _timelines;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _timelines = context.World.Query(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>()
            .OptionalBuffer<TimelineStage>(QueryAccess.Read));

    public void OnUpdate(ref ImmediateSystemContext context) =>
        context.World.ExecuteQuery(_timelines, context.LastSystemVersion, static rows =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                bool hasStages = chunk.HasBuffer<TimelineStage>();
                foreach (QueryRow row in chunk.Rows)
                {
                    ref Timeline timeline = ref row.ReadWrite<Timeline>();
                    if (hasStages && ContinueStages(ref timeline, row.ReadBuffer<TimelineStage>().AsSpan()))
                        continue;
                    if (timeline.Running != 0)
                        continue;
                    timeline.Completions++;
                    row.SetComponentEnabled<Timeline>(false);
                }
            }
        });

    internal static bool ContinueStages(ref Timeline timeline, ReadOnlySpan<TimelineStage> stages)
    {
        DevelopmentCheck.Require(stages.Length <= ushort.MaxValue, "A timeline stage table must fit its stage index.");
        if (timeline.Paused && timeline.Stage < stages.Length)
            return true;
        while (timeline.Stage < stages.Length)
        {
            TimelineStage stage = stages[timeline.Stage];
            switch (stage.End)
            {
                case StageEnd.External:
                    return true;
                case StageEnd.Settled:
                    if (timeline.Running != 0 || timeline.Time < stage.Duration)
                        return true;
                    timeline.Stage++;
                    timeline.Time = 0;
                    return timeline.Stage < stages.Length;
                case StageEnd.Duration:
                    DevelopmentCheck.Require(float.IsFinite(stage.Duration) && stage.Duration >= 0, "Stage duration must be finite and nonnegative.");
                    if (timeline.Time < stage.Duration)
                        return true;
                    timeline.Time -= stage.Duration;
                    timeline.Stage++;
                    timeline.Running = 0;
                    if (timeline.Stage < stages.Length && stages[timeline.Stage].End == StageEnd.Settled)
                        return true;
                    break;
                default:
                    DevelopmentCheck.Fail("A timeline stage requires a defined boundary type.");
                    return false;
            }
        }
        return false;
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_timelines);
}
