using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>One character's autoregressive history, generated local pose and decoded motion.</summary>
public sealed class FlowMatchingState
{
    internal readonly FlowMatchingModel Model;
    internal readonly Skeleton Skeleton;
    internal readonly float[] Previous, Transported, Control, Noise, Decoded;
    internal readonly BoneTransform[] Pose;
    internal bool Active, Sample, SuppliedNoise;
    internal float Delta;
    internal RootMotionDelta Motion = RootMotionDelta.Identity;
    private Vector3 _rootPosition;
    private Quaternion _rootRotation;
    private uint _random;
    public ReadOnlySpan<float> History => Previous;
    public ReadOnlySpan<float> PoseFeatures => Decoded;
    public ReadOnlySpan<BoneTransform> LocalPose => Pose;
    public RootMotionDelta RootMotion => Motion;

    internal FlowMatchingState(FlowMatchingModel model, Skeleton skeleton)
    {
        Model = model;
        Skeleton = skeleton;
        Previous = new float[model.LatentCount];
        Transported = new float[model.LatentCount];
        Control = new float[model.ControlCount];
        Noise = new float[model.LatentCount];
        Decoded = new float[model.Data.PoseOffset.Length];
        Pose = new BoneTransform[model.BoneCount];
    }

    internal void Reset(ReadOnlySpan<float> history, uint seed)
    {
        (history.IsEmpty ? Model.InitialHistory : history).CopyTo(Previous);
        Skeleton.ReferencePose.CopyTo(Pose);
        _rootPosition = Pose[0].Translation;
        _rootRotation = Pose[0].Rotation;
        _random = seed;
        Motion = RootMotionDelta.Identity;
    }

    internal void StartSample()
    {
        if (!SuppliedNoise)
            for (int sample = 0; sample < Noise.Length; sample++)
                Noise[sample] = MathF.Sqrt(-2 * MathF.Log(Uniform())) * MathF.Cos(2 * MathF.PI * Uniform());
        for (int value = 0; value < Transported.Length; value++)
            Transported[value] = Noise[value] * Model.Data.LatentDeviation[value];
    }

    private float Uniform()
    {
        _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5;
        return ((_random >> 8) + .5f) / 16777216f;
    }

    internal void Decode()
    {
        FlowMatchingData data = Model.Data;
        for (int value = 0; value < Decoded.Length; value++)
            Decoded[value] = Decoded[value] * data.PoseDeviation[value] * data.PoseScale[value] + data.PoseOffset[value];
        Matrix4x4 previous = Matrix4x4.CreateFromQuaternion(_rootRotation) * Matrix4x4.CreateTranslation(_rootPosition);
        _rootPosition += Delta * Vector3.Transform(new(Decoded[0], Decoded[1], Decoded[2]), _rootRotation);
        Vector3 angular = Delta * Vector3.Transform(new(Decoded[3], Decoded[4], Decoded[5]), _rootRotation);
        float angle = angular.Length();
        Quaternion delta = angle == 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(angular / angle, angle);
        _rootRotation = Quaternion.Normalize(Quaternion.Concatenate(_rootRotation, delta));
        Pose[0].Translation = _rootPosition;
        Pose[0].Rotation = _rootRotation;
        Pose[1].Translation = new(Decoded[6], Decoded[7], Decoded[8]);
        for (int bone = 1; bone < Pose.Length; bone++)
        {
            int start = 9 + (bone - 1) * 6;
            Vector3 x = Vector3.Normalize(new(Decoded[start], Decoded[start + 2], Decoded[start + 4]));
            Vector3 y = new(Decoded[start + 1], Decoded[start + 3], Decoded[start + 5]);
            y = Vector3.Normalize(y - x * Vector3.Dot(x, y));
            Vector3 z = Vector3.Cross(x, y);
            Pose[bone].Rotation = Quaternion.CreateFromRotationMatrix(new(
                x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1));
        }
        Matrix4x4 current = Matrix4x4.CreateFromQuaternion(_rootRotation) * Matrix4x4.CreateTranslation(_rootPosition);
        Matrix4x4.Invert(previous, out Matrix4x4 inverse);
        Motion = new(current * inverse);
    }
}
