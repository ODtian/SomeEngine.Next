using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Runs after evaluators: completes timelines with an empty running mask.</summary>
public struct TimelineEndSystem : ISystem<ImmediateSystemContext>
{
    private Query _timelines;
    private List<SomeEngine.ECS.Entities.Entity> _completed;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _completed = [];
        _timelines = context.World.Query(new QueryDescription().With<Timeline>().Optional<Buffer<TimelineStage>>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _completed.Clear();
        using (QueryScope rows = _timelines.Since(context.LastSystemVersion).Open())
            foreach (QueryChunk chunk in rows)
            {
                Span<Timeline> timelines = chunk.Write<Timeline>();
                bool hasStages = chunk.Has<Buffer<TimelineStage>>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    ref Timeline timeline = ref timelines[row];
                    if (timeline.Paused) continue;
                    if (hasStages && ContinueStages(ref timeline, chunk.ReadBuffer<TimelineStage>(row).AsSpan())) continue;
                    if (timeline.Running != 0) continue;
                    timeline.Completions++;
                    _completed.Add(chunk.Entities[row]);
                }
            }
        foreach (var entity in _completed) context.World.Disable<Timeline>(entity);
    }

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

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
