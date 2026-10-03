using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

public enum TrackFields : byte { Properties, LayerWeights }

/// <summary>Samples property tracks with the value operations selected by the caller.</summary>
public struct TrackSystem<T, TOps>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    private readonly TrackFields _fields = fields;
    private QueryHandle _tracks;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _tracks = context.World.Query(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<Track<T>>()
            .OptionalBuffer<Keyframe>(QueryAccess.Read));

    public void OnUpdate(ref ImmediateSystemContext context) =>
        Evaluate(context.World, context.LastSystemVersion, _fields == TrackFields.LayerWeights);

    private void Evaluate(World world, uint lastVersion, bool weightChannels)
    {
        var evaluation = (World: world, WeightChannels: weightChannels);
        world.ExecuteQuery(_tracks, lastVersion, ref evaluation,
            static (QueryCursor rows, ref (World World, bool WeightChannels) evaluation) =>
        {
            World world = evaluation.World;
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
                        bool weightChannel = previous.Channel.Component == ComponentId<AnimationLayers>.Value;
                        if (weightChannel != evaluation.WeightChannels)
                            continue;
                        var target = previous.Target == default ? row.Entity : previous.Target;
                        if (previous.Target != default && !world.IsAlive(target))
                        {
                            tracks[i].Finished = true;
                            continue;
                        }
                        if (timeline.Paused)
                        {
                            timeline.Running |= 1;
                            if (world.HasBuffer<Contribution<T>>(target))
                                Append(world, target, row.Entity, (uint)i, in previous);
                            continue;
                        }
                        ref Track<T> track = ref tracks[i];
                        if (track.Endpoints == TrackEndpoints.Displacement)
                        {
                            T basis = track.From;
                            track.From = track.Current = TOps.Add(in basis, in track.To);
                            track.To = basis;
                            track.Endpoints = TrackEndpoints.DisplacementValues;
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
                        if (world.HasBuffer<Contribution<T>>(target))
                            Append(world, target, row.Entity, (uint)i, in track);
                        else
                            PropertyAnimation.WriteSingle<T, TOps>(world, target, track.Channel, track.Layer, ReadValue(world, target, in track));
                        if (!finished)
                            timeline.Running |= 1;
                    }
                }
            }
        });
    }

    private static void Append(World world, Entity target, Entity source, uint output, in Track<T> track)
    {
        var contribution = new Contribution<T>
        {
            Channel = track.Channel, Layer = track.Layer, Value = ReadValue(world, target, in track),
            Source = source, Output = output,
        };
        world.ExecuteBufferWrite<Contribution<T>, Contribution<T>>(target, ref contribution,
            static (DynamicBuffer<Contribution<T>> values, ref Contribution<T> value) => values.Add(value));
    }

    private static T ReadValue(World world, Entity target, in Track<T> track)
    {
        T value = track.Current;
        if (track.Endpoints == TrackEndpoints.Values || track.Channel.Component == ComponentId<AnimationLayers>.Value ||
            !world.Has<AnimationLayers>(target))
            return value;
        AnimationLayers layers = world.Read<AnimationLayers>(target);
        DevelopmentCheck.Require(layers.Count <= 8 && track.Layer < layers.Count,
            "A property track requires a defined animation layer.");
        if (layers.Items[track.Layer].Mode != LayerMode.Additive)
            return value;
        T reference = track.Endpoints == TrackEndpoints.Displacement ? track.From : track.To;
        T basis = reference;
        PropertyAnimation.ReadBase(world, target, track.Channel, ref basis);
        T displacement = TOps.Subtract(in value, in reference);
        return TOps.Add(in basis, in displacement);
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_tracks);
}
