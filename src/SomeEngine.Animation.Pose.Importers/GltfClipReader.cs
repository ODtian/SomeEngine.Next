using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importers;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace SomeEngine.Animation.Pose.Importers;

/// <summary>Reads source TRS curves for the selected skeleton, shared by clip encoding importers.</summary>
public static class GltfClipReader
{
    /// <summary>Maps source node identities to the selected skeleton's joint and ancestor indices.</summary>
    public static Dictionary<Node, short> GetBoneIndices(Skin skin, Skeleton skeleton)
    {
        var bones = new Dictionary<Node, short>();
        int jointIndex = 0;
        foreach (Node joint in skin.Joints)
        {
            short bone = skeleton.JointBones[jointIndex++];
            for (Node? node = joint; node is not null && bone >= 0 && bones.TryAdd(node, bone); node = node.VisualParent)
                bone = skeleton.Parents[bone];
        }
        return bones;
    }

    public static RawClipData Read(GltfAnimation source, Skin skin, Skeleton skeleton,
        int rootMotionNode = -1, CancellationToken cancellationToken = default)
    {
        ModelRoot model = source.LogicalParent;
        Dictionary<Node, short> bones = GetBoneIndices(skin, skeleton);

        JsonNode animation = JsonNode.Parse(model.GetJsonPreview())!["animations"]![source.LogicalIndex]!;
        JsonArray sourceSamplers = animation["samplers"]!.AsArray();
        var accessors = new GltfAccessorReader(model);
        var times = new float[sourceSamplers.Count][];
        float duration = 0;
        for (int index = 0; index < times.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VertexStream input = accessors.ReadComponents(model.LogicalAccessors[sourceSamplers[index]!["input"]!.GetValue<int>()], cancellationToken);
            float[] keys = new float[input.Count];
            for (int key = 0; key < keys.Length; key++) keys[key] = (float)input.Values.Span[key];
            times[index] = keys;
            duration = Math.Max(duration, keys[^1]);
        }

        var channels = ImmutableArray.CreateBuilder<RawClipChannel>();
        foreach (JsonNode? channel in animation["channels"]!.AsArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonNode target = channel!["target"]!;
            if (target["node"] is not JsonNode nodeIndex || !bones.TryGetValue(model.LogicalNodes[nodeIndex.GetValue<int>()], out short bone))
                continue;
            string path = target["path"]!.GetValue<string>();
            if (path is not ("translation" or "rotation" or "scale")) continue;
            BoneTransformProperty property = path switch
            {
                "translation" => BoneTransformProperty.Translation,
                "rotation" => BoneTransformProperty.Rotation,
                _ => BoneTransformProperty.Scale,
            };
            int samplerIndex = channel["sampler"]!.GetValue<int>();
            JsonNode sampler = sourceSamplers[samplerIndex]!;
            ClipInterpolation interpolation = sampler["interpolation"]?.GetValue<string>() switch
            {
                "STEP" => ClipInterpolation.Step,
                "CUBICSPLINE" => ClipInterpolation.CubicSpline,
                _ => ClipInterpolation.Linear,
            };
            VertexStream output = accessors.ReadComponents(model.LogicalAccessors[sampler["output"]!.GetValue<int>()], cancellationToken);
            var values = new Vector4[output.Count];
            ReadOnlySpan<double> components = output.Values.Span;
            for (int key = 0; key < values.Length; key++)
            {
                int offset = key * output.ComponentCount;
                values[key] = new((float)components[offset], (float)components[offset + 1], (float)components[offset + 2],
                    output.ComponentCount == 4 ? (float)components[offset + 3] : 0);
            }
            channels.Add(new(bone, property, interpolation, times[samplerIndex], values));
        }

        ImmutableArray<ClipEvent> events = source.Extras?["someengine_events"] is JsonArray markers
            ? markers.Select(marker => new ClipEvent(marker!["time"]!.GetValue<float>(), marker["id"]!.GetValue<ushort>()))
                .OrderBy(marker => marker.Time).ToImmutableArray()
            : [];
        short root = rootMotionNode < 0 ? (short)-1 : bones[model.LogicalNodes[rootMotionNode]];
        return new(duration, channels.ToImmutable(), events, root);
    }
}
