using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
    private static void UpdateDirectBoundary<T>(World world, Entity animation, int index,
        in Track<T> track, in Timeline timeline, MotionBoundary boundary) where T : unmanaged
    {
        var summary = (ValueType: BufferComponents.Header<Track<T>>(), Track: index, TrackData: track,
            Timeline: timeline, Boundary: boundary, Stage: new TimelineStage());
        world.ExecuteBufferWrite<DirectBoundary,
            (int ValueType, int Track, Track<T> TrackData, Timeline Timeline, MotionBoundary Boundary, TimelineStage Stage)>(animation, ref summary,
            static (boundaries, ref summary) =>
            {
                for (int index = 0; index < boundaries.Count; index++)
                {
                    DirectBoundary value = boundaries.Read(index);
                    if (value.ValueType == summary.ValueType && value.Track == summary.Track)
                    {
                        value.Boundary = summary.Boundary;
                        value.Start = summary.TrackData.Start;
                        value.Stage = summary.TrackData.Stage;
                        value.Starts = summary.TrackData.StartId;
                        value.Defined = true;
                        boundaries[index] = value;
                    }
                }
                foreach (DirectBoundary value in boundaries.AsSpan())
                {
                    if (!value.Defined || value.Starts != summary.Timeline.Starts || value.Stage != summary.Timeline.Stage) continue;
                    summary.Stage.Duration = Math.Max(summary.Stage.Duration, value.Start + value.Boundary.Duration);
                    if (value.Boundary.End == StageEnd.External || value.Boundary.End == StageEnd.Settled && summary.Stage.End == StageEnd.Duration)
                        summary.Stage.End = value.Boundary.End;
                }
            });
        var stage = (Index: (int)timeline.Stage, Stage: summary.Stage);
        world.ExecuteBufferWrite<TimelineStage, (int Index, TimelineStage Stage)>(animation, ref stage,
            static (stages, ref stage) =>
            {
                while (stages.Count > stage.Index + 1) stages.SwapRemoveAt(stages.Count - 1);
                while (stages.Count <= stage.Index) stages.Add(default);
                stages[stage.Index] = stage.Stage;
            });
    }
}
