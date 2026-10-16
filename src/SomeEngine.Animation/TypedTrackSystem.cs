using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Evaluates one complete concrete algorithm in batches over its typed ECS storage.</summary>
public struct TypedTrackSystem<T, TOps, TParameters, TState, TEvaluator>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    private readonly TrackFields _fields = fields;
    private QueryHandle _tracks;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _tracks = context.World.Query(context.World.QueryDefinition().ReadWrite<Timeline>().WriteBuffer<Track<T>>()
            .WriteBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>());

    public unsafe void OnUpdate(ref ImmediateSystemContext context)
    {
        var evaluation = (World: context.World, Weights: _fields == TrackFields.LayerWeights);
        context.World.ExecuteQuery(_tracks, context.LastSystemVersion, ref evaluation, &Evaluate);

        static void Evaluate(QueryCursor rows, ref (World World, bool Weights) evaluation)
        {
                World world = evaluation.World;
                int type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type;
                foreach (QueryRow row in rows.Rows)
                {
                    DynamicBuffer<Track<T>> tracks = row.Buffer<Track<T>>();
                    Span<MotionData<T, TParameters, TState>> data = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Data(
                        row.Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>());
                    for (int index = 0; index < data.Length; index++) data[index].Active = false;
                    ref Timeline timeline = ref row.ReadWrite<Timeline>();
                    bool enabled = world.IsEnabled<Timeline>(row.Entity);
                    bool evaluate = false;
                    for (int index = 0; index < tracks.Count; index++)
                    {
                        Track<T> track = tracks.Read(index);
                        if (track.MotionType != type || !track.SourceActive ||
                            (track.Channel.Component == ComponentId<AnimationLayers>.Value) != evaluation.Weights)
                            continue;
                        if (track.Finished || !enabled || timeline.Paused)
                        {
                            if (enabled && !track.Finished) timeline.Running |= 1;
                            if (track.Finished && PropertyAnimation.HoldsLayerValue(world, row.Entity, in track) || enabled)
                                PropertyAnimation.SubmitTyped<T, TOps>(world, row.Entity, (uint)index, in track);
                            continue;
                        }
                        ref MotionData<T, TParameters, TState> motion = ref data[track.MotionIndex];
                        motion.Values = new(track.From, track.To, track.Current);
                        motion.Time = new(timeline.Time - track.Start, timeline.Time - track.SampleTime);
                        motion.Active = true;
                        evaluate = true;
                    }
                    if (!evaluate) continue;
                    var batch = new MotionBatch<T, TParameters, TState>(data);
                    TEvaluator.EvaluateBatch<TEvaluator>(ref batch);
                    for (int index = 0; index < tracks.Count; index++)
                    {
                        ref Track<T> track = ref tracks[index];
                        if (track.MotionType != type || !track.SourceActive || track.Finished ||
                            (track.Channel.Component == ComponentId<AnimationLayers>.Value) != evaluation.Weights)
                            continue;
                        ref MotionData<T, TParameters, TState> motion = ref data[track.MotionIndex];
                        track.Current = motion.Sample.Value;
                        track.Finished = motion.Sample.Finished;
                        track.SampleTime = timeline.Time;
                        PropertyAnimation.SubmitTyped<T, TOps>(world, row.Entity, (uint)index, in track);
                        if (track.Finished)
                        {
                            motion.Active = false;
                        }
                        else timeline.Running |= 1;
                    }
                }
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_tracks);
}
