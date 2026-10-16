using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Combines compiled output ranges after every concrete algorithm has sampled.</summary>
public struct GraphOutputSystem<T, TOps>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    private readonly TrackFields _fields = fields;
    private QueryHandle _outputs;
    private FieldWrite<T>[] _writes = null!;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _writes = ArrayPool<FieldWrite<T>>.Shared.Rent(64);
        _outputs = context.World.Query(context.World.QueryDefinition().Read<Timeline>().ReadBuffer<Track<T>>()
            .ReadBuffer<GraphFieldOutput<T>>().ReadBuffer<GraphFieldTrack<T>>()
            .OptionalBuffer<PropertySource<T>>(QueryAccess.ReadWrite).Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<BaseValue<T>>(QueryAccess.Read).OptionalBuffer<LayerChannel>(QueryAccess.Read)
            .OptionalBuffer<Contribution<T>>(QueryAccess.ReadWrite)
            .OptionalBuffer<GraphMotion>(QueryAccess.Read));
    }

    public unsafe void OnUpdate(ref ImmediateSystemContext context)
    {
        var evaluation = (World: context.World, Weights: _fields == TrackFields.LayerWeights, Writes: _writes, Count: 0);
        context.World.ExecuteQuery(_outputs, context.LastSystemVersion, ref evaluation, &Evaluate);
        _writes = evaluation.Writes;
        FieldWrites<T>.Flush(context.World, _writes, ref evaluation.Count);
    }

    private static void Evaluate(QueryCursor rows, ref (World World, bool Weights, FieldWrite<T>[] Writes, int Count) evaluation)
    {
        World world = evaluation.World;
        int count = 0;
        foreach (QueryChunkView chunk in rows.Chunks)
        {
            bool hasMotions = chunk.HasBuffer<GraphMotion>();
            foreach (QueryRow row in chunk.Rows)
            {
                Timeline timeline = row.Read<Timeline>();
                BufferView<Track<T>> tracks = row.ReadBuffer<Track<T>>();
                BufferView<GraphFieldTrack<T>> entries = row.ReadBuffer<GraphFieldTrack<T>>();
                ReadOnlySpan<GraphMotion> motions = hasMotions ? row.ReadBuffer<GraphMotion>().AsSpan() : default;
                foreach (GraphFieldOutput<T> output in row.ReadBuffer<GraphFieldOutput<T>>().AsSpan())
                {
                    Track<T> leader = tracks[output.Leader];
                    if ((leader.Channel.Component == ComponentId<AnimationLayers>.Value) != evaluation.Weights)
                        continue;
                    Entity target = leader.Target == Entity.Null ? row.Entity : leader.Target;
                    if (!world.IsAlive(target))
                        continue;
                    T value = default;
                    float total = 0;
                    Track<T> selected = default;
                    int selectedIndex = -1, motionId = -1;
                    for (int index = output.First; index < output.First + output.Count; index++)
                    {
                        int candidateIndex = entries[index].Track;
                        Track<T> candidate = tracks[candidateIndex];
                        if (candidate.GraphMotion != motionId)
                        {
                            AddSelected(world, target, in selected, selectedIndex, motionId, motions, ref value, ref total);
                            selectedIndex = -1;
                            motionId = candidate.GraphMotion;
                        }
                        if (candidate.CaptureStart || candidate.StartId != timeline.Starts || candidate.Typed && !candidate.SourceActive ||
                            candidate.GraphMotion > 0 && candidate.MotionStartId != motions[candidate.GraphMotion - 1].Starts)
                            continue;
                        selected = candidate;
                        selectedIndex = candidateIndex;
                    }
                    AddSelected(world, target, in selected, selectedIndex, motionId, motions, ref value, ref total);
                    if (total == 0)
                        continue;
                    leader.Current = value;
                    leader.Endpoints = TrackEndpoints.Values;
                    leader.Typed = true;
                    if (PropertyAnimation.TrySubmitTyped<T, TOps>(world, row.Entity, (uint)output.Leader, in leader, out FieldWrite<T> write))
                        FieldWrites<T>.Add(ref evaluation.Writes, ref count, write.Target, write.Channel, write.Value);
                }
            }
        }
        evaluation.Count = count;
    }

    private static void AddSelected(World world, Entity target, in Track<T> selected, int selectedIndex, int motionId,
        ReadOnlySpan<GraphMotion> motions, ref T value, ref float total)
    {
        if (selectedIndex < 0)
            return;
        float weight = motionId == 0 ? 1 : motions[motionId - 1].Weight;
        if (weight <= 0)
            return;
        T sample = selected.Current;
        if (selected.Endpoints != TrackEndpoints.Values && selected.Channel.Component != ComponentId<AnimationLayers>.Value &&
            world.Has<AnimationLayers>(target) && world.Read<AnimationLayers>(target).Items[selected.Layer].Mode == LayerMode.Additive)
        {
            T basis = selected.To;
            PropertyAnimation.ReadBase(world, target, selected.Channel, ref basis);
            T displacement = TOps.Subtract(in sample, in selected.To);
            sample = TOps.Add(in basis, in displacement);
        }
        total += weight;
        value = TOps.Lerp(in value, in sample, weight / total);
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        context.World.ReleaseQuery(_outputs);
        ArrayPool<FieldWrite<T>>.Shared.Return(_writes);
    }
}
