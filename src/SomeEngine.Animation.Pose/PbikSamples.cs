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

    internal PbikSamples(Scheduler scheduler, PbikRig rig, SkeletonInstance skeleton)
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
            JobResourceAccess.Read(scheduler.GetResourceToken(rig)),
            JobResourceAccess.Read(scheduler.GetResourceToken(rig.Skeleton)),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
        ];
    }

    internal void ReadParents()
    {
        for (int bone = 0; bone < Transforms.Length; bone++)
            if (_rig.Skeleton.InheritedParents[bone] >= 0) _inherited[bone] = _parents[bone].Transform;
    }

    internal void Solve()
    {
        var solver = new PbikSolver(_rig, Transforms, Targets, _models, _inherited,
            _positions, _anchors, _goals, _forwardPositions, _rotations);
        solver.Solve();
    }
}