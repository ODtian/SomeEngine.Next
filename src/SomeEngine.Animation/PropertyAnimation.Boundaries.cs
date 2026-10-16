using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
    private static void UpdateDirectBoundary<T>(World world, Entity animation, int index,
        in Track<T> track, in Timeline timeline, MotionBoundary boundary) where T : unmanaged
    {
        var update = (ValueType: BufferComponents.Header<Track<T>>(), Track: index, TrackData: track, Boundary: boundary);
        world.ExecuteBufferWrite<DirectBoundary,
            (int ValueType, int Track, Track<T> TrackData, MotionBoundary Boundary)>(animation, ref update,
            static (boundaries, ref update) =>
            {
                for (int index = 0; index < boundaries.Count; index++)
                {
                    DirectBoundary value = boundaries.Read(index);
                    if (value.ValueType == update.ValueType && value.Track == update.Track)
                    {
                        value.Boundary = update.Boundary;
                        value.Start = update.TrackData.Start;
                        value.Stage = update.TrackData.Stage;
                        value.Starts = update.TrackData.StartId;
                        value.Defined = true;
                        boundaries[index] = value;
                    }
                }
            });
        WriteDirectStage(world, animation, in timeline);
    }

    internal static void RemoveDirectBoundary<T>(World world, Entity animation, int index, in Timeline timeline) where T : unmanaged
    {
        var source = (ValueType: BufferComponents.Header<Track<T>>(), Track: index);
        world.ExecuteBufferWrite<DirectBoundary, (int ValueType, int Track)>(animation, ref source,
            static (boundaries, ref source) =>
            {
                for (int index = 0; index < boundaries.Count; index++)
                    if (boundaries.Read(index).ValueType == source.ValueType && boundaries.Read(index).Track == source.Track)
                        boundaries[index].Defined = false;
            });
        WriteDirectStage(world, animation, in timeline);
    }

    private static void WriteDirectStage(World world, Entity animation, in Timeline timeline)
    {
        var summary = (Timeline: timeline, Stage: new TimelineStage());
        world.ExecuteBufferRead<DirectBoundary, (Timeline Timeline, TimelineStage Stage)>(animation, ref summary,
            static (boundaries, ref summary) =>
            {
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
                while (stages.Count <= stage.Index) stages.Add(default);
                stages[stage.Index] = stage.Stage;
            });
    }
}
