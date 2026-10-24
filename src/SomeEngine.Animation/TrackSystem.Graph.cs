using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public partial struct TrackSystem<T, TOps>
{
    private static void SampleGraph(World world, Entity animation, DynamicBuffer<Track<T>> tracks,
        DynamicBuffer<GraphMotion> motions, GraphData[] graph, uint starts, bool paused, bool weightChannels,
        ReadOnlySpan<Keyframe> keyframes)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            Track<T> previous = tracks.Read(i);
            if (previous.Typed || previous.GraphMotion == 0 || previous.StartId != starts ||
                (previous.Channel.Component == ComponentId<AnimationLayers>.Value) != weightChannels)
                continue;
            ref GraphMotion motion = ref motions[previous.GraphMotion - 1];
            if (motion.Weight <= 0)
                continue;
            Entity target = previous.Target == Entity.Null ? animation : previous.Target;
            if (previous.Target != Entity.Null && !world.IsAlive(target))
                continue;
            GraphMotionData source = graph[0].Motions![previous.GraphMotion - 1];
            var sample = GraphTimes.Project(source.Stages.AsSpan(0, source.StageCount), motion.Stage, motion.Time);
            ref Track<T> track = ref tracks[i];
            if (track.MotionStartId != motion.Starts)
            {
                track.MotionStartId = motion.Starts;
                track.CaptureStart = true;
                track.Finished = false;
                track.SampleTime = track.Start;
                track.Velocity = default;
                if (track.Endpoints == TrackEndpoints.DisplacementValues)
                {
                    track.Endpoints = TrackEndpoints.Displacement;
                    track.To = track.Displacement;
                }
            }
            if (paused || track.Finished || track.MotionStage < motion.Stage || track.MotionStage > sample.Stage)
                continue;
            float time = motion.Time;
            for (int stage = motion.Stage; stage < track.MotionStage; stage++)
                time -= source.Stages![stage].Duration;
            if (track.MotionStage < sample.Stage)
                time = source.Stages![track.MotionStage].Duration;
            if (time < track.Start)
            {
                motion.Running = true;
                continue;
            }
            SampleValue(world, animation, target, tracks, i, ref track, track.MotionStage, time, starts, keyframes);
            motion.Running |= !track.Finished;
        }
        if (world.HasBuffer<GraphFieldOutput<T>>(animation))
            return;
        for (int i = 0; i < tracks.Count; i++)
        {
            Track<T> track = tracks.Read(i);
            if (track.GraphMotion == 0 || track.StartId != starts ||
                (track.Channel.Component == ComponentId<AnimationLayers>.Value) != weightChannels)
                continue;
            Entity target = track.Target == Entity.Null ? animation : track.Target;
            if (!world.IsAlive(target) || HasEarlierGraphOutput(tracks, i, in track))
                continue;
            T value = default;
            float total = 0;
            for (int j = 0; j < tracks.Count; j++)
            {
                Track<T> candidate = tracks.Read(j);
                if (candidate.GraphMotion == 0 || candidate.GraphGroup != track.GraphGroup || candidate.Target != track.Target ||
                    candidate.Channel != track.Channel || candidate.Layer != track.Layer || candidate.StartId != starts || candidate.CaptureStart)
                    continue;
                GraphMotion motion = motions.Read(candidate.GraphMotion - 1);
                if (motion.Weight <= 0 || HasLaterGraphValue(tracks, j, in candidate, in motion))
                    continue;
                T sample = ReadValue(world, target, in candidate);
                total += motion.Weight;
                value = TOps.Lerp(in value, in sample, motion.Weight / total);
            }
            if (total == 0)
                continue;
            if (world.HasBuffer<Contribution<T>>(target))
            {
                var contribution = new Contribution<T>
                {
                    Channel = track.Channel, Layer = track.Layer, Value = value, Source = animation, Output = (uint)i,
                };
                world.ExecuteBufferWrite<Contribution<T>, Contribution<T>>(target, ref contribution,
                    static (DynamicBuffer<Contribution<T>> buffer, ref Contribution<T> value) => buffer.Add(value));
            }
            else
                PropertyAnimation.WriteSingle<T, TOps>(world, target, track.Channel, track.Layer, value);
        }
    }

    private static bool HasEarlierGraphOutput(DynamicBuffer<Track<T>> tracks, int index, in Track<T> track)
    {
        for (int i = 0; i < index; i++)
        {
            Track<T> earlier = tracks.Read(i);
            if (earlier.GraphGroup == track.GraphGroup && earlier.Target == track.Target && earlier.Channel == track.Channel &&
                earlier.Layer == track.Layer && earlier.StartId == track.StartId)
                return true;
        }
        return false;
    }

    private static bool HasLaterGraphValue(DynamicBuffer<Track<T>> tracks, int index, in Track<T> track, in GraphMotion motion)
    {
        for (int i = index + 1; i < tracks.Count; i++)
        {
            Track<T> later = tracks.Read(i);
            if (later.GraphMotion == track.GraphMotion && later.Target == track.Target && later.Channel == track.Channel &&
                later.Layer == track.Layer && later.StartId == track.StartId && !later.CaptureStart &&
                (later.MotionStage > track.MotionStage || later.MotionStage == track.MotionStage && later.Start >= track.Start))
                return true;
        }
        return false;
    }
}
