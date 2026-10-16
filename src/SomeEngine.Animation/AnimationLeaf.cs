using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

/// <summary>A leaf declares its boundary and installs concrete instance data through the public World API.</summary>
public interface IAnimationLeaf<TParameters> where TParameters : struct
{
    static abstract MotionBoundary Describe(in TParameters parameters);
    static virtual LayerChannel Output(in TParameters parameters) => default;
    static virtual object? Prepare(ReadOnlySpan<TParameters> parameters) => null;
    static virtual void Complete(World world, Entity animation) { }
    static virtual void Release(World world, Entity animation) { }
    static abstract void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<TParameters> definitions, int index, in AnimationLeafPlacement placement);
}

/// <summary>Compiled parameters and derived data shared by every instance of this leaf batch.</summary>
public sealed class AnimationLeafBatch<TParameters> where TParameters : struct
{
    private readonly TParameters[] _parameters;

    internal AnimationLeafBatch(TParameters[] parameters, object? data) => (_parameters, Data) = (parameters, data);

    public ReadOnlySpan<TParameters> Parameters => _parameters;
    public object? Data { get; }
}

public readonly record struct AnimationLeafPlacement(
    ushort Stage, float Start, int Motion, int Group, ushort MotionStage,
    uint Starts, byte Layer, int Output, int Capacity);

internal abstract class GraphLeafData
{
    internal abstract Type Type { get; }
    internal abstract object CreateBatch(Span<GraphTrack> tracks, int part);
    internal abstract void Complete(World world, Entity animation);
}

internal sealed class GraphLeafData<TParameters, TLeaf>(in TParameters parameters) : GraphLeafData
    where TParameters : struct
    where TLeaf : struct, IAnimationLeaf<TParameters>
{
    internal readonly TParameters Parameters = parameters;
    internal override Type Type => typeof(GraphLeafData<TParameters, TLeaf>);
    internal override void Complete(World world, Entity animation) => TLeaf.Complete(world, animation);

    internal override object CreateBatch(Span<GraphTrack> tracks, int part)
    {
        int count = 0;
        foreach (GraphTrack track in tracks)
            if (track.Part == part && track.Node.Leaf?.Type == Type)
                count++;
        var parameters = new TParameters[count];
        int index = 0;
        for (int trackIndex = 0; trackIndex < tracks.Length; trackIndex++)
        {
            ref GraphTrack track = ref tracks[trackIndex];
            if (track.Part != part || track.Node.Leaf?.Type != Type)
                continue;
            parameters[index] = ((GraphLeafData<TParameters, TLeaf>)track.Node.Leaf).Parameters;
            track.LeafIndex = index++;
        }
        var batch = new AnimationLeafBatch<TParameters>(parameters, TLeaf.Prepare(parameters));
        for (int trackIndex = 0; trackIndex < tracks.Length; trackIndex++)
            if (tracks[trackIndex].Part == part && tracks[trackIndex].Node.Leaf?.Type == Type)
                tracks[trackIndex].LeafBatch = batch;
        return batch;
    }
}

internal static unsafe class GraphLeaves<TParameters, TLeaf>
    where TParameters : struct
    where TLeaf : struct, IAnimationLeaf<TParameters>
{
    internal static void Install(World world, Entity animation, in GraphTrack source, byte[] values, uint starts, Entity replacement)
    {
        GraphReleases.Add(world, animation, &Release);
        Entity target = replacement == Entity.Null ? source.Node.Target : replacement;
        if (source.Node.DefinesLayer)
            GraphLeafLayers.Apply(world, target, source.Node.Layer, source.Node.LayerDefinition);
        var definitions = (AnimationLeafBatch<TParameters>)source.LeafBatch!;
        var placement = new AnimationLeafPlacement(source.Stage, source.Start, source.Motion, source.Group,
            source.MotionStage, starts, source.Node.Layer, source.Output, definitions.Parameters.Length);
        TLeaf.Install(world, animation, target, definitions, source.LeafIndex, in placement);
    }

    private static void Release(World world, Entity animation) => TLeaf.Release(world, animation);
}

internal static class GraphLeafLayers
{
    internal static void Apply(World world, Entity target, byte layer, AnimationLayer definition)
    {
        bool exists = world.Has<AnimationLayers>(target);
        AnimationLayers layers = exists ? world.Read<AnimationLayers>(target) : default;
        while (layers.Count <= layer)
            layers.Items[layers.Count++] = new AnimationLayer { Weight = 1 };
        layers.Items[layer] = definition;
        if (exists)
            world.Replace(target, layers);
        else
            world.Add(target, layers);
    }
}
