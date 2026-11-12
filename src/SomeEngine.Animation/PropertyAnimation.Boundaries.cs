using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
    private static void UpdateDirectBoundary<T>(World world, Entity animation, int index,
        in Track<T> track, in Timeline timeline, MotionBoundary boundary) where T : unmanaged
    {
        var update = (ValueType: ComponentId<Buffer<Track<T>>>.Value, Track: index, TrackData: track, Boundary: boundary);
        {
            using var boundaries = world.GetBuffer<DirectBoundary>(animation);
            for (int boundaryIndex = 0; boundaryIndex < boundaries.Count; boundaryIndex++)
            {
                DirectBoundary value = boundaries[boundaryIndex];
                if (value.ValueType == update.ValueType && value.Track == update.Track)
                {
                    value.Boundary = update.Boundary;
                    value.Start = update.TrackData.Start;
                    value.Stage = update.TrackData.Stage;
                    value.Starts = update.TrackData.StartId;
                    value.Defined = true;
                    boundaries[boundaryIndex] = value;
                }
            }
        }
        WriteDirectStage(world, animation, in timeline);
    }

    internal static void RemoveDirectBoundary<T>(World world, Entity animation, int index, in Timeline timeline) where T : unmanaged
    {
        var source = (ValueType: ComponentId<Buffer<Track<T>>>.Value, Track: index);
        {
            using var boundaries = world.GetBuffer<DirectBoundary>(animation);
            for (int boundaryIndex = 0; boundaryIndex < boundaries.Count; boundaryIndex++)
                if (boundaries[boundaryIndex].ValueType == source.ValueType && boundaries[boundaryIndex].Track == source.Track)
                    boundaries[boundaryIndex].Defined = false;
        }
        WriteDirectStage(world, animation, in timeline);
    }

    private static void WriteDirectStage(World world, Entity animation, in Timeline timeline)
    {
        var summary = (Timeline: timeline, Stage: new TimelineStage());
        {
            using var boundaries = world.ReadBuffer<DirectBoundary>(animation);
            foreach (DirectBoundary value in boundaries.AsSpan())
            {
                if (!value.Defined || value.Starts != summary.Timeline.Starts || value.Stage != summary.Timeline.Stage) continue;
                summary.Stage.Duration = Math.Max(summary.Stage.Duration, value.Start + value.Boundary.Duration);
                if (value.Boundary.End == StageEnd.External || value.Boundary.End == StageEnd.Settled && summary.Stage.End == StageEnd.Duration)
                    summary.Stage.End = value.Boundary.End;
            }
        }
        var stage = (Index: (int)timeline.Stage, Stage: summary.Stage);
        {
            using var stages = world.GetBuffer<TimelineStage>(animation);
            while (stages.Count <= stage.Index) stages.Add(default);
            stages[stage.Index] = stage.Stage;
        }
    }
}
