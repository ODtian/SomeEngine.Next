using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public unsafe ref partial struct AnimationBuilder
{
    public void Leaf<TParameters, TLeaf>(Entity target, in TParameters parameters, byte layer = 0)
        where TParameters : struct
        where TLeaf : struct, IAnimationLeaf<TParameters>
    {
        ref BuildData data = ref _data![0];
        var node = new GraphNode
        {
            Kind = GraphNodeKind.Track, Target = target, Layer = layer,
            Leaf = new GraphLeafData<TParameters, TLeaf>(in parameters),
            Boundary = TLeaf.Describe(in parameters), Field = TLeaf.Output(in parameters),
            Install = &GraphLeaves<TParameters, TLeaf>.Install,
        };
        for (int parent = data.Current; parent != 0; parent = data.Nodes![parent].Parent)
            if (data.Nodes![parent].Kind == GraphNodeKind.Layer)
            {
                node.Layer = data.Nodes[parent].Layer;
                node.LayerDefinition = data.Nodes[parent].LayerDefinition;
                node.DefinesLayer = true;
                break;
            }
        GraphBuilding.Add(_data, node);
    }

    public void To<T, TOps, TParameters, TState, TEvaluator>(Entity target, Channel<T> channel, T value,
        Spec<T, TOps, TParameters, TState, TEvaluator> spec, byte layer = 0)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        var parameters = new PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator>(channel, value, spec, TrackEndpoints.Values);
        Leaf<PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator>, PropertyLeaf<T, TOps, TParameters, TState, TEvaluator>>(
            target, in parameters, layer);
    }

    public void By<T, TOps, TParameters, TState, TEvaluator>(Entity target, Channel<T> channel, T value,
        Spec<T, TOps, TParameters, TState, TEvaluator> spec, byte layer = 0)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        var parameters = new PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator>(channel, value, spec, TrackEndpoints.Displacement);
        Leaf<PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator>, PropertyLeaf<T, TOps, TParameters, TState, TEvaluator>>(
            target, in parameters, layer);
    }
}

public readonly record struct PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator>(
    Channel<T> Channel, T Value, Spec<T, TOps, TParameters, TState, TEvaluator> Spec, TrackEndpoints Endpoints)
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>;

public readonly struct PropertyLeaf<T, TOps, TParameters, TState, TEvaluator> :
    IAnimationLeaf<PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator>>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    public static MotionBoundary Describe(in PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator> parameters) => parameters.Spec.Boundary;

    public static LayerChannel Output(in PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator> parameters) =>
        LayerChannel.Of(parameters.Channel);

    public static object Prepare(ReadOnlySpan<PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator>> definitions)
    {
        var parameters = new TParameters[definitions.Length];
        for (int index = 0; index < definitions.Length; index++)
            parameters[index] = definitions[index].Spec.Parameters;
        return parameters;
    }

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator>> definitions,
        int index, in AnimationLeafPlacement placement) =>
        GraphPropertyTracks<T, TOps, TParameters, TState, TEvaluator>.Install(world, animation, target,
            definitions.Parameters[index], (TParameters[])definitions.Data!, index, in placement);

    public static void Complete(World world, Entity animation) =>
        GraphPropertyTracks<T, TOps, TParameters, TState, TEvaluator>.Complete(world, animation);

    public static void Release(World world, Entity animation) =>
        GraphPropertyTracks<T, TOps, TParameters, TState, TEvaluator>.Release(world, animation);
}
