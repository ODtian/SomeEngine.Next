using System.Runtime.CompilerServices;
using SomeEngine.ECS;
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
            .OptionalBuffer<Keyframe>(QueryAccess.Read));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_tracks, context.LastSystemVersion, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                bool hasKeyframes = chunk.HasBuffer<Keyframe>();
                foreach (QueryRow row in chunk.Rows)
                {
                    ReadOnlySpan<Keyframe> keyframes = hasKeyframes ? row.ReadBuffer<Keyframe>().AsSpan() : default;
                    ref Timeline timeline = ref row.ReadWrite<Timeline>();
                    DynamicBuffer<Track<T>> tracks = row.Buffer<Track<T>>();
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        Track<T> previous = tracks.Read(i);
                        if (previous.Finished || previous.Stage != timeline.Stage || previous.StartId != timeline.Starts)
                            continue;
                        if (timeline.Paused)
                        {
                            timeline.Running |= 1;
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
                            finished = track.Curve.Step<T, TOps>(timeline.Time - track.SampleTime, in track.To, ref track.Current, ref track.Velocity);
                        }
                        else
                        {
                            float progress = track.Duration == 0 ? 1 : Math.Clamp((timeline.Time - track.Start) / track.Duration, 0, 1);
                            track.Current = progress == 1 ? track.To : TOps.Lerp(in track.From, in track.To, track.Curve.Sample(progress, keyframes));
                            finished = progress == 1;
                        }
                        track.SampleTime = timeline.Time;
                        track.Finished = finished;
                        var target = track.Target == default ? row.Entity : track.Target;
                        Unsafe.WriteUnaligned(ref world.WriteBytes(target, track.Channel.Component, track.Channel.Offset, Unsafe.SizeOf<T>()), track.Current);
                        if (!finished)
                            timeline.Running |= 1;
                    }
                }
            }
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_tracks);
}
