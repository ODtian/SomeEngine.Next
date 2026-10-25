using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public sealed class BodyMotion
{
    public DualQuaternion[] Bones { get; }
    public Matrix4x4[] BoneMatrices { get; }
    private readonly CharacterAsset character;
    private readonly int[] footVertices;
    private readonly MotionClip runningClip = new();
    private readonly Quaternion[] pose;
    private readonly Quaternion[] idleRotations;
    private readonly Quaternion[] boneCorrections;
    private readonly int[] motionBones;

    public BodyMotion(CharacterAsset character)
    {
        this.character = character;
        Bones = new DualQuaternion[character.Bones.Length];
        BoneMatrices = new Matrix4x4[Bones.Length];
        pose = new Quaternion[runningClip.BoneCount];
        idleRotations = new Quaternion[Bones.Length];
        boneCorrections = new Quaternion[Bones.Length];
        motionBones = new int[Bones.Length];
        for (int i = 0; i < Bones.Length; i++)
        {
            CharacterBone bone = character.Bones[i];
            string side = bone.Name.EndsWith(".L", StringComparison.Ordinal) ? "l" : "r";
            string source = bone.Name.Split('.')[0] switch
            {
                "root" => "root",
                "spine05" or "spine04" => "lowerback",
                "spine03" => "upperback",
                "spine02" or "spine01" => "thorax",
                "neck01" => "lowerneck",
                "neck02" or "neck03" => "upperneck",
                "head" => "head",
                "upperleg01" => side + "femur",
                "lowerleg01" => side + "tibia",
                "foot" => side + "foot",
                "upperarm01" => side + "humerus",
                "lowerarm01" => side + "radius",
                _ => "",
            };
            motionBones[i] = runningClip.FindBone(source);
            boneCorrections[i] = source is "" or "root" ? Quaternion.Identity
                : RotateBetween(bone.Tail - bone.Head, runningClip.Direction(motionBones[i]));
            Quaternion idle = bone.Name.StartsWith("upperarm01", StringComparison.Ordinal)
                ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, side == "l" ? -.48f : .48f) : Quaternion.Identity;
            idleRotations[i] = bone.Parent < 0 ? idle : idleRotations[bone.Parent] * idle;
        }
        footVertices = Enumerable.Range(0, character.Body.Positions.Length).Where(index => character.Body.Positions[index].Y < .12f).ToArray();
        Update(0, 0, 0);
    }

    public void Update(float seconds, float running, float spread)
    {
        float height = runningClip.Sample(seconds, pose);
        for (int i = 0; i < Bones.Length; i++)
        {
            CharacterBone bone = character.Bones[i];
            string name = bone.Name;
            float side = name.EndsWith(".L", StringComparison.Ordinal) ? 1 : -1;
            int source = motionBones[i];
            Quaternion rotation = source < 0 ? Bones[bone.Parent].Rotation
                : Quaternion.Slerp(idleRotations[i], pose[source] * boneCorrections[i], running);
            if (name.StartsWith("upperleg01", StringComparison.Ordinal) || name.StartsWith("lowerleg01", StringComparison.Ordinal)
                || name.StartsWith("foot.", StringComparison.Ordinal))
                rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, side * spread) * rotation;
            Vector3 head = bone.Parent < 0 ? bone.Head : Bones[bone.Parent].Transform(bone.Head);
            if (bone.Parent < 0) head.Y += (height / runningClip.ReferenceHeight - 1) * bone.Head.Y * running;
            Bones[i] = DualQuaternion.FromRotationTranslation(rotation, head - Vector3.Transform(bone.Head, rotation));
        }
        float lowest = float.PositiveInfinity;
        foreach (int vertex in footVertices)
            lowest = MathF.Min(lowest, Blend(character.Body.BoneWeights[vertex]).Transform(character.Body.Positions[vertex]).Y);
        float lift = MathF.Max(0, .009f - lowest);
        DualQuaternion translation = DualQuaternion.FromRotationTranslation(Quaternion.Identity, new(0, lift, 0));
        for (int i = 0; i < Bones.Length; i++)
        {
            Bones[i] = translation * Bones[i];
            BoneMatrices[i] = Matrix4x4.CreateFromQuaternion(Bones[i].Rotation)
                * Matrix4x4.CreateTranslation(Bones[i].Translation);
        }
    }

    public DualQuaternion Blend(ReadOnlySpan<BoneInfluence> weights)
    {
        DualQuaternion result = default;
        Quaternion reference = Bones[weights[0].Bone].Real;
        foreach (BoneInfluence influence in weights)
        {
            DualQuaternion transform = Bones[influence.Bone];
            float weight = Quaternion.Dot(reference, transform.Real) < 0 ? -influence.Weight : influence.Weight;
            result += transform * weight;
        }
        return result;
    }

    public Matrix4x4 BlendMatrix(ReadOnlySpan<BoneInfluence> weights)
    {
        Matrix4x4 result = default;
        foreach (BoneInfluence influence in weights) result += BoneMatrices[influence.Bone] * influence.Weight;
        return result;
    }

    private static Quaternion RotateBetween(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        float cosine = Vector3.Dot(from, to);
        if (cosine < -.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + cosine));
    }

    public static float Smooth(float start, float end, float value)
    {
        float blend = Math.Clamp((value - start) / (end - start), 0, 1);
        return blend * blend * (3 - 2 * blend);
    }
}
