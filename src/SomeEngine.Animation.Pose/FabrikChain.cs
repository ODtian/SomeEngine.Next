using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>A bone's outgoing direction cone, expressed in its parent's coordinates with an angle in radians.</summary>
public readonly record struct FabrikJointLimit(int Bone, Vector3 Axis, float MaximumAngle);

/// <summary>Shared parent-to-child bone indices, joint limits and convergence settings for a FABRIK chain.</summary>
public sealed class FabrikChain
{
    private readonly int[] _bones;
    private readonly FabrikJointLimit[] _limits;
    internal readonly FabrikCone[] Cones;

    public Skeleton Skeleton { get; }
    public ReadOnlySpan<int> Bones => _bones;
    public ReadOnlySpan<FabrikJointLimit> Limits => _limits;
    public float Tolerance { get; }
    public int Iterations { get; }

    public FabrikChain(Skeleton skeleton, ReadOnlySpan<int> bones, ReadOnlySpan<FabrikJointLimit> limits = default,
        float tolerance = .001f, int iterations = 16)
    {
        Skeleton = skeleton;
        _bones = bones.ToArray();
        _limits = limits.ToArray();
        Tolerance = tolerance;
        Iterations = iterations;
        Cones = new FabrikCone[bones.Length - 1];
        Array.Fill(Cones, new FabrikCone(Vector3.UnitX, -1, 0));
        foreach (FabrikJointLimit limit in limits)
            Cones[Array.IndexOf(_bones, limit.Bone)] = new(Vector3.Normalize(limit.Axis),
                MathF.Cos(limit.MaximumAngle), MathF.Sin(limit.MaximumAngle));
    }
}

internal readonly record struct FabrikCone(Vector3 Axis, float Cosine, float Sine);

internal sealed class FabrikSamples
{
    internal readonly BoneTransform[] Transforms;
    internal readonly JobResourceAccess[] Accesses;
    private readonly FabrikChain _chain;
    private readonly Matrix4x4[] _models, _originals, _solved, _inherited;
    private readonly BonePose[] _parents;
    private readonly Vector3[] _positions, _directions;
    private readonly float[] _lengths;

    internal FabrikSamples(Scheduler scheduler, FabrikChain chain, SkeletonInstance skeleton)
    {
        _chain = chain;
        Transforms = new BoneTransform[chain.Skeleton.BoneCount];
        _models = new Matrix4x4[Transforms.Length];
        _inherited = new Matrix4x4[Transforms.Length];
        _parents = new BonePose[Transforms.Length];
        _originals = new Matrix4x4[chain.Bones.Length];
        _solved = new Matrix4x4[chain.Bones.Length];
        _positions = new Vector3[chain.Bones.Length];
        _directions = new Vector3[chain.Bones.Length - 1];
        _lengths = new float[chain.Bones.Length - 1];
        for (int bone = 0; bone < Transforms.Length; bone++)
            if (chain.Skeleton.InheritedParents[bone] >= 0)
                _parents[bone] = skeleton.ModelPose.GetBone(chain.Skeleton.InheritedParents[bone]);
        Accesses =
        [
            JobResourceAccess.Read(scheduler.GetResourceToken(chain)),
            JobResourceAccess.Read(scheduler.GetResourceToken(chain.Skeleton)),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
        ];
    }

    internal void ReadParents()
    {
        for (int bone = 0; bone < Transforms.Length; bone++)
            if (_chain.Skeleton.InheritedParents[bone] >= 0) _inherited[bone] = _parents[bone].Transform;
    }

    internal void Solve(Vector3 target)
    {
        for (int bone = 0; bone < Transforms.Length; bone++)
            _models[bone] = Transforms[bone].ToMatrix() * ParentModel(bone);
        ReadOnlySpan<int> bones = _chain.Bones;
        for (int joint = 0; joint < bones.Length; joint++)
        {
            _originals[joint] = _solved[joint] = _models[bones[joint]];
            _positions[joint] = _originals[joint].Translation;
        }
        for (int joint = 0; joint < _lengths.Length; joint++)
        {
            Vector3 segment = _positions[joint + 1] - _positions[joint];
            _lengths[joint] = segment.Length();
            _directions[joint] = segment / _lengths[joint];
        }
        Vector3 root = _positions[0];
        Matrix4x4 rootParent = ParentModel(bones[0]);
        float toleranceSquared = _chain.Tolerance * _chain.Tolerance;
        for (int iteration = 0; iteration < _chain.Iterations; iteration++)
        {
            _positions[^1] = target;
            for (int joint = _lengths.Length - 1; joint >= 0; joint--)
            {
                Matrix4x4 parent = joint == 0 ? rootParent : _solved[joint - 1];
                Vector3 direction = LimitDirection(joint, _positions[joint + 1] - _positions[joint], parent);
                _positions[joint] = _positions[joint + 1] - direction * _lengths[joint];
                TurnJoint(joint, direction);
            }
            _positions[0] = root;
            for (int joint = 0; joint < _lengths.Length; joint++)
            {
                Matrix4x4 parent = joint == 0 ? rootParent : _solved[joint - 1];
                Vector3 direction = LimitDirection(joint, _positions[joint + 1] - _positions[joint], parent);
                _positions[joint + 1] = _positions[joint] + direction * _lengths[joint];
                TurnJoint(joint, direction);
            }
            if (Vector3.DistanceSquared(_positions[^1], target) <= toleranceSquared) break;
        }
        int chainJoint = 0;
        for (int bone = 0; bone < Transforms.Length; bone++)
        {
            Matrix4x4 parent = ParentModel(bone);
            if (chainJoint < _lengths.Length && bone == bones[chainJoint])
            {
                Matrix4x4.Invert(parent, out Matrix4x4 inverseParent);
                Matrix4x4 local = _solved[chainJoint] * inverseParent;
                Matrix4x4.Decompose(local, out _, out Quaternion rotation, out _);
                Transforms[bone].Rotation = Quaternion.Normalize(rotation);
                chainJoint++;
            }
            _models[bone] = Transforms[bone].ToMatrix() * parent;
        }
    }

    private Matrix4x4 ParentModel(int bone)
    {
        int parent = _chain.Skeleton.Parents[bone];
        if (parent >= 0) return _models[parent];
        return _chain.Skeleton.InheritedParents[bone] >= 0 ? _inherited[bone] : Matrix4x4.Identity;
    }

    private Vector3 LimitDirection(int joint, Vector3 direction, in Matrix4x4 parent)
    {
        direction = Vector3.Normalize(direction);
        FabrikCone cone = _chain.Cones[joint];
        if (cone.Cosine == -1) return direction;
        Matrix4x4.Invert(parent, out Matrix4x4 inverseParent);
        Vector3 local = Vector3.Normalize(Vector3.TransformNormal(direction, inverseParent));
        float cosine = Math.Clamp(Vector3.Dot(local, cone.Axis), -1, 1);
        if (cosine >= cone.Cosine) return direction;
        Vector3 tangent = local - cone.Axis * cosine;
        tangent = tangent.LengthSquared() > 1e-12f ? Vector3.Normalize(tangent) : FabrikMath.Perpendicular(cone.Axis);
        Vector3 constrained = cone.Axis * cone.Cosine + tangent * cone.Sine;
        return Vector3.Normalize(Vector3.TransformNormal(constrained, parent));
    }

    private void TurnJoint(int joint, Vector3 direction)
    {
        Matrix4x4 basis = _originals[joint];
        basis.Translation = Vector3.Zero;
        Matrix4x4 rotated = basis * Matrix4x4.CreateFromQuaternion(FabrikMath.FromTo(_directions[joint], direction));
        rotated.Translation = _positions[joint];
        _solved[joint] = rotated;
    }
}

internal static class FabrikMath
{
    internal static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        Vector3 cross = Vector3.Cross(from, to);
        float sine = cross.Length();
        if (sine == 0) return dot >= 0 ? Quaternion.Identity : new Quaternion(Perpendicular(from), 0);
        return Quaternion.CreateFromAxisAngle(cross / sine, MathF.Atan2(sine, dot));
    }

    internal static Vector3 Perpendicular(Vector3 direction) => Vector3.Normalize(
        Vector3.Cross(direction, MathF.Abs(direction.X) < .9f ? Vector3.UnitX : Vector3.UnitY));
}
