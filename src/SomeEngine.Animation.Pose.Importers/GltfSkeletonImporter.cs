using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;

namespace SomeEngine.Animation.Pose.Importers;

public sealed record GltfSkeletonSettings;

[JsonSerializable(typeof(GltfSkeletonSettings))]
public sealed partial class GltfSkeletonSettingsJson : JsonSerializerContext;

public sealed class GltfSkeletonSettingsProvider : IAssetImporterSettings<GltfSkeletonSettings>
{
    public GltfSkeletonSettings DefaultSettings => new();
    public JsonTypeInfo<GltfSkeletonSettings> TypeInfo => GltfSkeletonSettingsJson.Default.GltfSkeletonSettings;
}

/// <summary>Imports each skin's joints and ancestors in parent-before-child order.</summary>
[AssetImporter("b82962a0-6277-45c8-9bc7-145592010a87", typeof(SkeletonBlock),
    SettingsProvider = typeof(GltfSkeletonSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class GltfSkeletonImporter : IAssetImporter<Skin, GltfSkeletonSettings>
{
    public static readonly Guid Id = new("b82962a0-6277-45c8-9bc7-145592010a87");

    public ValueTask ImportAsync(Skin source, GltfSkeletonSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Walking ancestors first preserves local transforms, including nodes between joints.
        var nodes = new List<Node>();
        var boneIndices = new Dictionary<Node, short>();
        var ancestors = new Stack<Node>();
        foreach (Node joint in source.Joints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (Node? node = joint; node is not null && !boneIndices.ContainsKey(node); node = node.VisualParent)
                ancestors.Push(node);
            while (ancestors.TryPop(out Node? node))
            {
                if (nodes.Count > short.MaxValue) throw new InvalidDataException("The skin exceeds the 16-bit bone index range.");
                boneIndices.Add(node, (short)nodes.Count);
                nodes.Add(node);
            }
        }

        var parents = new short[nodes.Count];
        var referencePose = new BoneTransform[nodes.Count];
        var nameHashes = new uint[nodes.Count];
        for (int bone = 0; bone < nodes.Count; bone++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Node node = nodes[bone];
            parents[bone] = node.VisualParent is { } parent ? boneIndices[parent] : (short)-1;
            var transform = node.LocalTransform.GetDecomposed();
            referencePose[bone] = new() { Translation = transform.Translation, Rotation = transform.Rotation, Scale = transform.Scale };
            nameHashes[bone] = BoneNames.Hash(node.Name.AsSpan());
        }
        var jointBones = new short[source.JointsCount];
        int index = 0;
        foreach (Node joint in source.Joints) jointBones[index++] = boneIndices[joint];
        var skeleton = new SkeletonBlock(parents, referencePose, nameHashes, jointBones);
        try { output.SetBlock(skeleton); }
        catch { skeleton.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }
}
