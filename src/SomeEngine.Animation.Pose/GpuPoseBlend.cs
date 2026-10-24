namespace SomeEngine.Animation.Pose;

public abstract record GpuBlendNode;

public sealed record GpuBlendClip(QuantizedClipData Clip, bool Loop = false, float Speed = 1) : GpuBlendNode;

/// <summary>A shared BlendSpace and its uniformly quantized clips, with coordinates from GraphParameters.</summary>
public sealed record GpuBlendSpace : GpuBlendNode
{
    private readonly QuantizedClipData[] _clips;
    public BlendSpace Space { get; }
    public byte ParameterX { get; }
    public byte ParameterY { get; }
    public ReadOnlySpan<QuantizedClipData> Clips => _clips;

    public GpuBlendSpace(BlendSpace space, byte parameterX, byte parameterY = 0, float sampleRate = 30)
    {
        (Space, ParameterX, ParameterY) = (space, parameterX, parameterY);
        _clips = new QuantizedClipData[space.Samples.Length];
        for (int sample = 0; sample < _clips.Length; sample++)
            _clips[sample] = QuantizedClipData.Encode(space.Samples[sample].Clip, sampleRate);
    }
}

public sealed record GpuBlendLayer(GpuBlendNode Base, GpuBlendNode Source, BoneMask Mask,
    LayerMode Mode = LayerMode.Override, float Weight = 1, int WeightParameter = -1) : GpuBlendNode;

/// <summary>One shared operation with its source indices resolved before GPU execution.</summary>
public readonly record struct GpuBlendStep(GpuBlendNode Node, int Base = -1, int Source = -1);

/// <summary>Dependency-ordered local-pose operations sharing a skeleton, clips and ready masks.</summary>
public sealed class GpuPoseBlend
{
    private readonly GpuBlendStep[] _steps;
    public Skeleton Skeleton { get; }
    public ReadOnlySpan<GpuBlendStep> Steps => _steps;
    public int InputCount { get; }

    public GpuPoseBlend(Skeleton skeleton, GpuBlendNode output)
    {
        Skeleton = skeleton;
        var steps = new List<GpuBlendStep>();
        var indices = new Dictionary<GpuBlendNode, int>(ReferenceEqualityComparer.Instance);
        Add(output);
        _steps = steps.ToArray();
        foreach (GpuBlendStep step in _steps)
            InputCount = Math.Max(InputCount, step.Node switch
            {
                GpuBlendSpace space => Math.Max(space.ParameterX, space.ParameterY) + 1,
                GpuBlendLayer layer => layer.WeightParameter + 1,
                _ => 0,
            });

        int Add(GpuBlendNode node)
        {
            if (indices.TryGetValue(node, out int index)) return index;
            int basis = -1, source = -1;
            if (node is GpuBlendLayer layer) { basis = Add(layer.Base); source = Add(layer.Source); }
            index = steps.Count;
            indices.Add(node, index);
            steps.Add(new(node, basis, source));
            return index;
        }
    }
}
