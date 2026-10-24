// Weighted heading construction and damped CCD adapted from Everything-Will-Be-IK.
// Copyright (c) 2016 Eron Gjoni. MIT license: external/ewbik/LICENSE.
using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

internal sealed class EwbikSamples
{
    internal readonly BoneTransform[] Transforms;
    internal readonly EwbikTarget[] Targets;
    internal readonly JobResourceAccess[] Accesses;
    internal EwbikSettings Settings;
    private readonly EwbikRig _rig;
    private readonly Matrix4x4[] _models, _inherited;
    private readonly Quaternion[] _rotations;
    private readonly BonePose[] _parents;
    private readonly EwbikRotationFitting _fitting = new();

    internal EwbikSamples(EwbikRig rig, SkeletonInstance skeleton)
    {
        _rig = rig;
        Transforms = new BoneTransform[rig.Skeleton.BoneCount];
        Targets = new EwbikTarget[rig.Effectors.Length];
        Settings = rig.Settings;
        _models = new Matrix4x4[Transforms.Length];
        _inherited = new Matrix4x4[Transforms.Length];
        _rotations = new Quaternion[Transforms.Length];
        _parents = new BonePose[Transforms.Length];
        for (int bone = 0; bone < Transforms.Length; bone++)
            if (rig.Skeleton.InheritedParents[bone] >= 0)
                _parents[bone] = skeleton.ModelPose.GetBone(rig.Skeleton.InheritedParents[bone]);
        Accesses =
        [
            JobResourceAccess.Read(JobSystem.Default.GetResourceToken(rig)),
            JobResourceAccess.Read(JobSystem.Default.GetResourceToken(rig.Skeleton)),
            JobResourceAccess.Write(JobSystem.Default.GetResourceToken(this)),
        ];
    }

    internal void ReadParents()
    {
        for (int bone = 0; bone < Transforms.Length; bone++)
            if (_rig.Skeleton.InheritedParents[bone] >= 0) _inherited[bone] = _parents[bone].Transform;
    }

    internal void Solve()
    {
        for (int bone = 0; bone < Transforms.Length; bone++) UpdateModel(bone);
        for (int iteration = 0; iteration < Settings.Iterations; iteration++)
        {
            for (int jointIndex = _rig.Joints.Length - 1; jointIndex >= 0; jointIndex--)
            {
                EwbikJoint joint = _rig.Joints[jointIndex];
                if (joint.DampingScale == 0) continue;
                Vector3 pivot = _models[joint.Bone].Translation;
                _fitting.Reset();
                for (int index = joint.InfluenceStart; index < joint.InfluenceStart + joint.InfluenceCount; index++)
                {
                    EwbikInfluence influence = _rig.Influences[index];
                    EwbikTarget target = Targets[influence.Effector];
                    float weight = influence.Weight * target.Weight;
                    if (weight == 0) continue;
                    EwbikEffector effector = _rig.Effectors[influence.Effector];
                    Vector3 from = _models[effector.Bone].Translation - pivot, to = target.Position - pivot;
                    _fitting.Add(from, to, weight);
                    AddAxis(Vector3.UnitX, target.AxisPriorities.X);
                    AddAxis(Vector3.UnitY, target.AxisPriorities.Y);
                    AddAxis(Vector3.UnitZ, target.AxisPriorities.Z);

                    void AddAxis(Vector3 axis, float priority)
                    {
                        if (priority == 0) return;
                        Vector3 sourceAxis = Vector3.Transform(axis, _rotations[effector.Bone]) * effector.AxisLength;
                        Vector3 targetAxis = Vector3.Transform(axis, target.Orientation) * effector.AxisLength;
                        _fitting.Add(from + sourceAxis, to + targetAxis, weight * priority);
                        _fitting.Add(from - sourceAxis, to - targetAxis, weight * priority);
                    }
                }
                Quaternion delta = _fitting.Fit();
                float angle = 2 * MathF.Atan2(new Vector3(delta.X, delta.Y, delta.Z).Length(), delta.W);
                float limit = Settings.Damping * joint.DampingScale;
                if (angle > limit) delta = Quaternion.Slerp(Quaternion.Identity, delta, limit / angle);
                Matrix4x4.Decompose(ParentModel(joint.Bone), out _, out Quaternion parentRotation, out _);
                Transforms[joint.Bone].Rotation = Quaternion.Normalize(Quaternion.Conjugate(parentRotation)
                    * delta * _rotations[joint.Bone]);
                for (int index = joint.DescendantStart; index < joint.DescendantStart + joint.DescendantCount; index++)
                    UpdateModel(_rig.Descendants[index]);
            }
            if (Reached()) break;
        }
    }

    private bool Reached()
    {
        float distanceSquared = Settings.PositionTolerance * Settings.PositionTolerance;
        float cosine = MathF.Cos(Settings.OrientationTolerance);
        for (int effector = 0; effector < Targets.Length; effector++)
        {
            EwbikTarget target = Targets[effector];
            if (target.Weight == 0) continue;
            int bone = _rig.Effectors[effector].Bone;
            if (Vector3.DistanceSquared(_models[bone].Translation, target.Position) > distanceSquared) return false;
            if (!AxisReached(Vector3.UnitX, target.AxisPriorities.X)
                || !AxisReached(Vector3.UnitY, target.AxisPriorities.Y)
                || !AxisReached(Vector3.UnitZ, target.AxisPriorities.Z)) return false;

            bool AxisReached(Vector3 axis, float priority) => priority == 0
                || Vector3.Dot(Vector3.Transform(axis, _rotations[bone]), Vector3.Transform(axis, target.Orientation)) >= cosine;
        }
        return true;
    }

    private void UpdateModel(int bone)
    {
        _models[bone] = Transforms[bone].ToMatrix() * ParentModel(bone);
        Matrix4x4.Decompose(_models[bone], out _, out _rotations[bone], out _);
    }

    private Matrix4x4 ParentModel(int bone) => _rig.Skeleton.Parents[bone] >= 0 ? _models[_rig.Skeleton.Parents[bone]]
        : _rig.Skeleton.InheritedParents[bone] >= 0 ? _inherited[bone] : Matrix4x4.Identity;
}
