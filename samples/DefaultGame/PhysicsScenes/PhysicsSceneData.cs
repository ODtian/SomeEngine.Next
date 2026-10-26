namespace DefaultGame;

internal enum SceneMaterialModel { RigidConstraints, CompliantMaterials }

internal sealed record SceneInputEvent(int Frame, int? Body = null, double[]? Velocity = null,
    int? RemovedJoint = null, double[]? Position = null);
internal sealed record SceneBodyData(double[] Position, double[] Rotation, double[] Size, double Mass,
    double[] Inertia, double Friction, double[] Velocity, double[] AngularVelocity);
internal sealed record SceneJointData(int BodyA, int BodyB, double[] PointA, double[] PointB, bool AngularFixed, int Source);
internal sealed record SceneCollisionPair(int BodyA, int BodyB);
internal sealed record SceneDistanceRange(int BodyA, int BodyB, double[] PointA, double[] PointB, double Minimum, double Maximum);
internal sealed record SceneMaterials(double ContactStiffness, double JointLinearStiffness,
    double JointAngularStiffness, double ContactDamping = 0);
internal sealed record PhysicsSceneData(double StepSeconds, double[] Gravity, SceneBodyData[] Bodies,
    SceneJointData[] Joints, SceneInputEvent[] Events, SceneCollisionPair[] CollisionExclusions,
    SceneDistanceRange[]? DistanceRanges = null, SceneMaterials? Materials = null,
    int ContactDetectionInterval = 0, bool ContactHistoryEnabled = false, int Substeps = 1,
    SceneMaterialModel MaterialModel = SceneMaterialModel.RigidConstraints);
