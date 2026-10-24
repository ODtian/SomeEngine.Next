using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

internal sealed class PbikSamples
{
    internal readonly BoneTransform[] Transforms;
    internal readonly PbikTarget[] Targets;
    internal readonly JobResourceAccess[] Accesses;
    private readonly PbikRig _rig;
    private readonly Matrix4x4[] _models, _inherited;
    private readonly BonePose[] _parents;
    private readonly Vector3[] _positions, _anchors, _goals, _forwardPositions;
    private readonly Quaternion[] _rotations;
    private Quaternion _rootParentRotation;

    internal PbikSamples(PbikRig rig, SkeletonInstance skeleton)
    {
        _rig = rig;
        Transforms = new BoneTransform[rig.Skeleton.BoneCount];
        Targets = new PbikTarget[rig.Effectors.Length];
        _models = new Matrix4x4[Transforms.Length];
        _inherited = new Matrix4x4[Transforms.Length];
        _parents = new BonePose[Transforms.Length];
        _positions = new Vector3[rig.Bodies.Length];
        _anchors = new Vector3[rig.Bodies.Length];
        _rotations = new Quaternion[rig.Bodies.Length];
        _forwardPositions = new Vector3[rig.Bodies.Length];
        _goals = new Vector3[Targets.Length];
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
        for (int bone = 0; bone < Transforms.Length; bone++)
            _models[bone] = Transforms[bone].ToMatrix() * ParentModel(bone);
        Matrix4x4.Decompose(ParentModel(_rig.Root), out _, out _rootParentRotation, out _);
        for (int body = 0; body < _rig.Bodies.Length; body++)
        {
            PbikBody definition = _rig.Bodies[body];
            Matrix4x4 model = _models[definition.Bone];
            _positions[body] = model.Translation;
            Matrix4x4.Decompose(model, out _, out _rotations[body], out _);
            if (definition.Parent >= 0)
                _anchors[body] = Vector3.Transform(_positions[body] - _positions[definition.Parent],
                    Quaternion.Conjugate(_rotations[definition.Parent]));
        }
        for (int effector = 0; effector < Targets.Length; effector++)
            _goals[effector] = Vector3.Lerp(_positions[_rig.EffectorBodies[effector]],
                Targets[effector].Position, Targets[effector].Weight);
        for (int body = 0; body < _rig.Bodies.Length; body++)
        {
            PbikBody definition = _rig.Bodies[body];
            Quaternion local = Quaternion.Slerp(Transforms[definition.Bone].Rotation, definition.PreferredRotation,
                definition.PreferredWeight * definition.RotationMobility);
            Quaternion parent = definition.Parent >= 0 ? _rotations[definition.Parent] : _rootParentRotation;
            _rotations[body] = Quaternion.Normalize(parent * LimitLocal(definition, local));
            if (definition.RotationMobility == 0)
                Matrix4x4.Decompose(_models[definition.Bone], out _, out _rotations[body], out _);
            if (definition.Parent >= 0)
                _positions[body] = _positions[definition.Parent] + Vector3.Transform(_anchors[body], parent);
            if (definition.PositionMobility == 0) _positions[body] = _models[definition.Bone].Translation;
        }
        Vector3 pull = Vector3.Zero;
        for (int effector = 0; effector < _goals.Length; effector++)
            pull += _goals[effector] - _positions[_rig.EffectorBodies[effector]];
        pull *= _rig.Bodies[0].PositionMobility / _goals.Length;
        for (int body = 0; body < _positions.Length; body++)
            if (_rig.Bodies[body].PositionMobility > 0) _positions[body] += pull;

        float toleranceSquared = _rig.Tolerance * _rig.Tolerance;
        for (int iteration = 0; iteration < _rig.Iterations; iteration++)
        {
            for (int effector = 0; effector < _goals.Length; effector++)
            {
                int body = _rig.EffectorBodies[effector];
                _positions[body] = Vector3.Lerp(_positions[body], _goals[effector], _rig.Bodies[body].PositionMobility);
            }
            for (int body = _positions.Length - 1; body > 0; body--) Connect(body);
            for (int body = 1; body < _positions.Length; body++) Connect(body);
            float error = 0;
            for (int body = 0; body < _positions.Length; body++)
            {
                PbikBody definition = _rig.Bodies[body];
                Quaternion parent = definition.Parent >= 0 ? _rotations[definition.Parent] : _rootParentRotation;
                Quaternion local = Quaternion.Conjugate(parent) * _rotations[body];
                _rotations[body] = Quaternion.Normalize(parent * LimitLocal(definition, local));
                _forwardPositions[body] = definition.Parent >= 0
                    ? _forwardPositions[definition.Parent] + Vector3.Transform(_anchors[body], parent)
                    : _positions[body];
                if (definition.PositionMobility == 0)
                    error = MathF.Max(error, Vector3.DistanceSquared(_positions[body], _forwardPositions[body]));
            }
            for (int effector = 0; effector < _goals.Length; effector++)
                error = MathF.Max(error, Vector3.DistanceSquared(_goals[effector], _forwardPositions[_rig.EffectorBodies[effector]]));
            if (error <= toleranceSquared) break;
        }
        for (int bone = 0; bone < Transforms.Length; bone++)
        {
            Matrix4x4 parent = ParentModel(bone);
            int body = _rig.BoneBodies[bone];
            if (body >= 0)
            {
                Matrix4x4.Decompose(parent, out _, out Quaternion parentRotation, out _);
                Transforms[bone].Rotation = Quaternion.Normalize(Quaternion.Conjugate(parentRotation) * _rotations[body]);
                if (bone == _rig.Root)
                {
                    Matrix4x4.Invert(parent, out Matrix4x4 inverseParent);
                    Transforms[bone].Translation = Vector3.Transform(_positions[body], inverseParent);
                }
            }
            _models[bone] = Transforms[bone].ToMatrix() * parent;
        }
    }

    private void Connect(int child)
    {
        PbikBody childBody = _rig.Bodies[child];
        int parent = childBody.Parent;
        PbikBody parentBody = _rig.Bodies[parent];
        Vector3 arm = Vector3.Transform(_anchors[child], _rotations[parent]);
        Vector3 error = _positions[parent] + arm - _positions[child];
        float length = error.Length();
        if (length == 0) return;
        Vector3 angular = Vector3.Cross(arm, error / length);
        float mobility = parentBody.PositionMobility + childBody.PositionMobility +
            parentBody.RotationMobility * angular.LengthSquared();
        if (mobility == 0) return;
        Vector3 impulse = -error / mobility;
        _positions[parent] += parentBody.PositionMobility * impulse;
        _positions[child] -= childBody.PositionMobility * impulse;
        Vector3 turn = parentBody.RotationMobility * Vector3.Cross(arm, impulse);
        _rotations[parent] = Quaternion.Normalize(_rotations[parent] +
            (new Quaternion(turn, 0) * _rotations[parent]) * .5f);
    }

    private Quaternion LimitLocal(in PbikBody body, Quaternion local)
    {
        if (!body.Limited) return local;
        Quaternion reference = _rig.Skeleton.ReferencePose[body.Bone].Rotation;
        Vector3 angles = PbikAngles.Euler(Quaternion.Conjugate(reference) * local);
        return reference * PbikAngles.Rotation(Vector3.Clamp(angles, body.Minimum, body.Maximum));
    }

    private Matrix4x4 ParentModel(int bone)
    {
        int parent = _rig.Skeleton.Parents[bone];
        if (parent >= 0) return _models[parent];
        return _rig.Skeleton.InheritedParents[bone] >= 0 ? _inherited[bone] : Matrix4x4.Identity;
    }
}
