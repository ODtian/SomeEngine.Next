using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Combines compiled output ranges after every concrete algorithm has sampled.</summary>
public struct GraphOutputSystem<T, TOps>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    private readonly TrackFields _fields = fields;
    private Query _outputs;
    private FieldWrite<T>[] _writes = null!;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _writes = ArrayPool<FieldWrite<T>>.Shared.Rent(64);
        _outputs = context.World.Query(new QueryDescription().WithStored<Timeline>().WithStored<Buffer<Track<T>>>()
            .WithStored<Buffer<GraphFieldOutput<T>>>().WithStored<Buffer<GraphFieldTrack<T>>>()
            .Optional<Buffer<PropertySource<T>>>().Optional<AnimationLayers>()
            .Optional<Buffer<BaseValue<T>>>().Optional<Buffer<LayerChannel>>()
            .Optional<Buffer<Contribution<T>>>()
            .Optional<Buffer<GraphMotion>>());
    }

    public unsafe void OnUpdate(ref ImmediateSystemContext context)
    {
        if (!(_outputs.Count != 0)) return;
        var evaluation = (World: context.World, Weights: _fields == TrackFields.LayerWeights, Writes: _writes, Count: 0);
        {
            using QueryScope read = _outputs.Since(context.LastSystemVersion).Open();
            Evaluate(in read, ref evaluation);
        }
        _writes = evaluation.Writes;
        FieldWrites<T>.Flush(context.World, _writes, ref evaluation.Count);
    }

    private static void Evaluate(scoped in QueryScope rows, ref (World World, bool Weights, FieldWrite<T>[] Writes, int Count) evaluation)
    {
        World world = evaluation.World;
        int count = 0;
        foreach (QueryChunk chunk in rows)
        {
            bool hasMotions = chunk.Has<Buffer<GraphMotion>>();
            chunk.TryRead<Timeline>(out var values0);
            for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
            {
                Timeline timeline = values0[rowIndex];
                ReadOnlyBuffer<Track<T>> tracks = chunk.ReadBuffer<Track<T>>(rowIndex);
                ReadOnlyBuffer<GraphFieldTrack<T>> entries = chunk.ReadBuffer<GraphFieldTrack<T>>(rowIndex);
                ReadOnlySpan<GraphMotion> motions = hasMotions ? chunk.ReadBuffer<GraphMotion>(rowIndex).AsSpan() : default;
                foreach (GraphFieldOutput<T> output in chunk.ReadBuffer<GraphFieldOutput<T>>(rowIndex).AsSpan())
                {
                    Track<T> leader = tracks[output.Leader];
                    if ((leader.Channel.Component == ComponentId<AnimationLayers>.Value) != evaluation.Weights)
                        continue;
                    Entity target = leader.Target == Entity.Null ? chunk.Entities[rowIndex] : leader.Target;
                    if (!world.IsAlive(target))
                        continue;
                    if (!world.IsEnabled<Timeline>(chunk.Entities[rowIndex]) && !world.Has<AnimationLayers>(target))
                    {
                        PropertyAnimation.FinishSource(world, chunk.Entities[rowIndex], in leader);
                        continue;
                    }
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
                    if (PropertyAnimation.TrySubmitTyped<T, TOps>(world, chunk.Entities[rowIndex], (uint)output.Leader, in leader, out FieldWrite<T> write))
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
            world.Has<AnimationLayers>(target) && world.ReadValue<AnimationLayers>(target).Items[selected.Layer].Mode == LayerMode.Additive)
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

        ArrayPool<FieldWrite<T>>.Shared.Return(_writes);
    }
}
