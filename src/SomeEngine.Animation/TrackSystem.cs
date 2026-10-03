using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Samples property tracks with the value operations selected by the caller.</summary>
public struct TrackSystem<T, TOps> : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    private QueryHandle _tracks;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _tracks = context.World.Query(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<Track<T>>()
            .OptionalBuffer<Keyframe>(QueryAccess.Read)
            .OptionalBuffer<TimelineStage>(QueryAccess.Read));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_tracks, context.LastSystemVersion, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                bool hasKeyframes = chunk.HasBuffer<Keyframe>();
                bool hasStages = chunk.HasBuffer<TimelineStage>();
                foreach (QueryRow row in chunk.Rows)
                {
                    ReadOnlySpan<Keyframe> keyframes = hasKeyframes ? row.ReadBuffer<Keyframe>().AsSpan() : default;
                    ReadOnlySpan<TimelineStage> stages = hasStages ? row.ReadBuffer<TimelineStage>().AsSpan() : default;
                    ref Timeline timeline = ref row.ReadWrite<Timeline>();
                    DynamicBuffer<Track<T>> tracks = row.Buffer<Track<T>>();
                    int stage = timeline.Stage;
                    float time = timeline.Time;
                    while (true)
                    {
                        bool duration = stage < stages.Length && stages[stage].End == StageEnd.Duration;
                        float sampleTime = duration ? Math.Min(time, stages[stage].Duration) : time;
                        bool running = SampleTracks(world, row.Entity, tracks, stage, sampleTime,
                            timeline.Starts, timeline.Paused, keyframes);
                        if (timeline.Paused || !duration || time < stages[stage].Duration)
                        {
                            if (running)
                                timeline.Running |= 1;
                            break;
                        }
                        time -= stages[stage].Duration;
                        stage++;
                        if (stage >= stages.Length || stages[stage].End == StageEnd.Settled)
                            break;
                    }
                }
            }
        });
    }

    private static bool SampleTracks(World world, Entity animation,
        DynamicBuffer<Track<T>> tracks, int stage, float time, uint starts, bool paused,
        ReadOnlySpan<Keyframe> keyframes)
    {
        bool running = false;
        for (int i = 0; i < tracks.Count; i++)
        {
            Track<T> previous = tracks.Read(i);
            if (previous.Finished || previous.Stage != stage || previous.StartId != starts)
                continue;
            var target = previous.Target == default ? animation : previous.Target;
            if (previous.Target != default && !world.IsAlive(target))
            {
                tracks[i].Finished = true;
                continue;
            }
            if (paused)
            {
                running = true;
                continue;
            }
            ref Track<T> track = ref tracks[i];
            if (track.Endpoints == TrackEndpoints.Displacement)
            {
                T basis = track.From;
                track.From = track.Current = TOps.Add(in basis, in track.To);
                track.To = basis;
                track.Endpoints = TrackEndpoints.Values;
            }
            bool finished;
            if (track.Curve.Kind == CurveKind.Spring)
            {
                finished = track.Curve.Step<T, TOps>(time - track.SampleTime, in track.To, ref track.Current, ref track.Velocity);
            }
            else
            {
                float progress = track.Duration == 0 ? 1 : Math.Clamp((time - track.Start) / track.Duration, 0, 1);
                track.Current = progress == 1 ? track.To : TOps.Lerp(in track.From, in track.To, track.Curve.Sample(progress, keyframes));
                finished = progress == 1;
            }
            track.SampleTime = time;
            track.Finished = finished;
            Unsafe.WriteUnaligned(ref world.WriteBytes(target, track.Channel.Component, track.Channel.Offset, Unsafe.SizeOf<T>()), track.Current);
            running |= !finished;
        }
        return running;
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_tracks);
}
