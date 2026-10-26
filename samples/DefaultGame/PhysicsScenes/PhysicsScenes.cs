namespace DefaultGame;

public enum PhysicsScene
{
    BepuPyramids = 200,
    BepuFriction = 201,
    JoltIslands = 202,
    JoltHeavyOnLight = 203,
    JoltStack = 204,
    IslandImpact = 205,
    JointIslands = 206,
    JoltRamp = 207,
    JoltImpulses = 208,
    MassColumns = 209,
    YawedColumns = 210,
    FreeCollisions = 211,
    SupportedBridge = 212,
    TaperedChain = 213,
    FreeTree = 214,
    ObliqueCollisions = 215,
    RotatingRamp = 216,
    SparseFlyers = 300,
    PlaneSeparatedCloud = 301,
    LongParallelBars = 302,
    StaticMaze = 303,
    CounterflowLanes = 304,
    OrthogonalStreams = 305,
    EnclosureFlyers = 306,
    ClusterMigration = 307,
    FaceDrop = 308,
    EdgeDrop = 309,
    CornerDrop = 310,
    EdgeCross = 311,
    CoplanarTiles = 312,
    StaggeredTiles = 313,
    ThinPlates = 314,
    NearlyParallelSlats = 315,
    PocketLanding = 316,
    UnequalFaceLanding = 317,
    RecontactHops = 318,
    SlidingOffEdge = 319,
    MovingFloor = 320,
    LoweredSupport = 321,
    ContactExchange = 322,
    RockingSlab = 323,
    RepeatedSideImpacts = 324,
    SeparationWave = 325,
    InclineHold = 326,
    InclineSlip = 327,
    CrossSlope = 328,
    SpinFriction = 329,
    StackDrag = 330,
    FrictionSeam = 331,
    StackTwist = 332,
    ReversingFriction = 333,
    OverhangBalance = 334,
    ThreeLegTable = 335,
    SplitFooting = 336,
    StairLoad = 337,
    HollowTower = 338,
    StackedBridges = 339,
    ForkSupport = 340,
    SupportWithdrawal = 341,
    SideBrace = 342,
    FloatingRaft = 343,
    ClosedSquare = 344,
    ClosedPentagon = 345,
    DoubleAnchoredCable = 346,
    JointLadder = 347,
    JointStar = 348,
    BinaryJointTree = 349,
    CombJointTree = 350,
    JointGrid = 351,
    AngularLockedTrain = 352,
    MixedJointLocks = 353,
    CounterweightedJoint = 354,
    JointReleaseCascade = 355,
    ChainOverPeg = 356,
    CableFloorLanding = 357,
    PendulumWall = 358,
    JointBridgeImpact = 359,
    LinkedCratesSlide = 360,
    JointGateWedge = 361,
    BranchPile = 362,
    TetheredCollision = 363,
    ManySingles = 364,
    ManyPairs = 365,
    UnevenIslands = 366,
    TouchingIslands = 367,
    SplittingIslands = 368,
    SharedAnchorChains = 369,
    MixedConstraintIslands = 370,
    MigratingImpact = 371,
    FreeSymmetricSpin = 372,
    FreeAsymmetricTumble = 373,
    SlenderRodSpin = 374,
    FastRotatingImpact = 375,
    GyroscopicChain = 376,
    SpinTransfer = 377
}

public static partial class PhysicsScenes
{
    internal static SceneDistanceRange[] CreateManualDistances()
    {
        (int First, int Last)[] pairs = [(0, 4), (0, 8), (0, 16), (0, 19), (4, 8), (8, 12), (12, 16), (16, 19)];
        return pairs.Select(pair => new SceneDistanceRange(pair.First, pair.Last,
            [0.5, 0, 0], [-0.5, 0, 0], 0, pair.Last - pair.First - 1)).ToArray();
    }

    // Source geometry and events live here; solver policy is world-wide.
    internal static PhysicsSceneData Create(PhysicsScene scene)
    {
        if ((int)scene >= 300) return CreateStress(scene);
        List<SceneBodyData> bodies = [];
        List<SceneJointData> joints = [];
        List<SceneInputEvent> events = [];
        double gravity = scene switch
        {
            PhysicsScene.JoltIslands or PhysicsScene.JoltHeavyOnLight or PhysicsScene.JoltStack
                or PhysicsScene.JoltRamp or PhysicsScene.JoltImpulses => -9.81,
            PhysicsScene.FreeCollisions or PhysicsScene.ObliqueCollisions => 0,
            _ => -10
        };
        switch (scene)
        {
            case PhysicsScene.BepuPyramids:
                Box(0, 0, -0.5, 2500, 2500, 1, 0);
                // Two complete 20-row instances from the upstream 40-instance demo.
                for (int pyramid = 0; pyramid < 2; ++pyramid)
                    for (int row = 0; row < 20; ++row)
                        for (int column = 0; column < 20 - row; ++column)
                            Box(-(20 - row) * 0.5 + column, (pyramid - 1) * 5, row + 0.5, 1, 1, 1, 1);
                break;
            case PhysicsScene.BepuFriction:
                Box(0, 0, -15, 2500, 2500, 30, 0, 1);
                for (int i = 0; i < 100; ++i)
                {
                    double friction = 0.75 * i / 99;
                    // AVBD uses geometric-mean materials; squaring gives the source pair coefficient.
                    Box(-80, i * 1.2, 0.5, 1, 1, 1, 1, friction * friction, vx: 20);
                }
                break;
            case PhysicsScene.JoltIslands:
                Box(0, 0, -1, 400, 400, 2, 0, 0.2);
                for (int i = 0; i < 10; ++i)
                    for (int j = i / 2; j < 10 - (i + 1) / 2; ++j)
                        for (int k = 0; k < 8; ++k)
                            Box(-10 + j * 2 + (i % 2), 8 * (k - 4), 1 + i * 2, 2, 2, 2, 8000, 0.2);
                break;
            case PhysicsScene.JoltHeavyOnLight:
                Box(0, 0, -1, 400, 400, 2, 0, 0.2);
                for (int i = 1; i <= 10; ++i)
                {
                    Box(-75 + i * 15, 0, 10, 10, 10, 10, 1_000_000, 0.2);
                    Box(-75 + i * 15, 0, 30, 10, 10, 10, 5_000_000 * i, 0.2);
                }
                break;
            case PhysicsScene.JoltStack:
                Box(0, 0, -1, 400, 400, 2, 0, 0.2);
                for (int i = 0; i < 10; ++i)
                    Box(10, 0, 1 + i * 2.1, 1, 4, 2, 8000, 0.2, angle: i % 2 * Math.PI * 0.5);
                break;
            case PhysicsScene.IslandImpact:
                Box(0, 0, -0.5, 40, 40, 1, 0);
                for (int island = 0; island < 2; ++island)
                    for (int row = 0; row < 3; ++row)
                        Box(0, island * 6, row + 0.5, 1, 1, 1, 1);
                Box(-5, 0, 0.5, 1, 1, 1, 4, 0);
                events.Add(new(180, 7, [6, 0, 0]));
                break;
            case PhysicsScene.JointIslands:
                for (int island = 0; island < 2; ++island)
                    for (int link = 0; link < 4; ++link)
                    {
                        Box(island * 5, 0, 8 - link, 0.5, 0.5, 1, link == 0 ? 0 : 1);
                        if (link > 0) joints.Add(new(bodies.Count - 2, bodies.Count - 1, [0, 0, -0.5], [0, 0, 0.5], false, joints.Count));
                    }
                events.Add(new(180, RemovedJoint: 0));
                break;
            case PhysicsScene.JoltRamp:
                Box(0, 0, 0, 200, 200, 2, 0, 1, pitch: Math.PI / 4);
                for (int i = 0; i <= 10; ++i)
                    Box(-50 + i * 10, 50, 55, 4, 4, 4, 64_000, i * 0.1, pitch: Math.PI / 4);
                break;
            case PhysicsScene.JoltImpulses:
                Box(0, 0, 0, 200, 200, 2, 0, 1);
                for (int i = 0; i <= 10; ++i)
                {
                    Box(-50 + i * 10, 50, 2.999, 6, 4, 4, 96_000, i * 0.1, angle: Math.PI / 4);
                    for (int frame = 0; frame < 360; ++frame)
                        events.Add(new(frame, i + 1, [0.15 / Math.Sqrt(2), -0.15 / Math.Sqrt(2), 0]));
                }
                break;
            case PhysicsScene.MassColumns:
                Box(0, 0, -0.5, 50, 30, 1, 0);
                for (int column = 0; column < 8; ++column)
                {
                    double height = 0.2;
                    for (int layer = 0; layer < 8; ++layer)
                    {
                        double size = 0.6 + 0.2 * (layer % 3);
                        Box((column - 3.5) * 4, 0, height + size / 2, 1.6, 1.3, size,
                            Math.Pow(4, (column + layer) % 5 - 2), angle: column * 0.17, vz: -(column % 3) * 0.25);
                        height += size + 0.07 * (layer % 3 + 1);
                    }
                }
                break;
            case PhysicsScene.YawedColumns:
                Box(0, 0, -0.5, 50, 30, 1, 0);
                for (int column = 0; column < 6; ++column)
                    for (int layer = 0; layer < 9; ++layer)
                        Box((column - 2.5) * 5, 0, 0.35 + layer * 0.8, 1.4, 2.8, 0.7,
                            0.5 + (column + layer) % 4, angle: column * 0.29 + layer * 0.37);
                break;
            case PhysicsScene.FreeCollisions:
                Box(0, 0, -100, 100, 100, 2, 0);
                for (int column = 0; column < 8; ++column)
                {
                    Box((column - 3.5) * 4, 0, 10, 1.5, 2, 1, Math.Pow(2, column - 3), vz: 2);
                    Box((column - 3.5) * 4, 0, 13, 1.5, 2, 1, 1, angle: column * 0.23, vz: -2);
                }
                break;
            case PhysicsScene.SupportedBridge:
                Box(0, 0, -0.5, 30, 30, 1, 0);
                Box(-1.5, 0, 1, 1, 1.5, 2, 1);
                Box(1.5, 0, 1, 1, 1.5, 2, 1);
                Box(0, 0, 2.25, 4.5, 1.5, 0.5, 3);
                Box(-1.25, 0, 3, 0.75, 1, 1, 5);
                Box(1.25, 0, 3, 0.75, 1, 1, 2);
                break;
            case PhysicsScene.TaperedChain:
                Box(0, 0, 20, 1, 1, 1, 0);
                double previousHeight = 20, previousHalf = 0.5;
                for (int link = 0; link < 12; ++link)
                {
                    double length = 0.8 + 0.1 * (link % 4);
                    double center = previousHeight - previousHalf - length / 2;
                    Box(0, 0, center, 0.35, 0.35, length, 0.5 + Math.Pow(2, link % 4));
                    joints.Add(new(link, link + 1, [0, 0, -previousHalf], [0, 0, length / 2], false, joints.Count));
                    previousHeight = center; previousHalf = length / 2;
                }
                events.Add(new(30, 12, [1.5, 0.7, 0]));
                events.Add(new(90, 5, [-0.5, 1, 0]));
                break;
            case PhysicsScene.FreeTree:
                Box(0, 0, 20, 0.8, 0.8, 1, 2);
                for (int branch = -1; branch <= 1; branch += 2)
                    for (int link = 0; link < 3; ++link)
                    {
                        int parent = link == 0 ? 0 : bodies.Count - 1;
                        Box(branch * 0.65, 0, 18.75 - link * 1.5, 0.5, 0.5, 1.5, 1 + link);
                        joints.Add(new(parent, bodies.Count - 1, link == 0 ? [branch * 0.4, 0, -0.5] : [0, 0, -0.75],
                            link == 0 ? [-branch * 0.25, 0, 0.75] : [0, 0, 0.75], false, joints.Count));
                    }
                events.Add(new(30, 3, [0.4, 0.8, 0]));
                events.Add(new(90, 6, [-0.4, -0.6, 0]));
                break;
            case PhysicsScene.ObliqueCollisions:
                for (int pair = 0; pair < 4; ++pair)
                {
                    Box(-2, pair * 4, pair * 0.1, 1, 0.8, 0.6, 1 + pair,
                        friction: 0.3, vx: 2, angle: 0.13 * (pair + 1));
                    Box(2, pair * 4 + 0.25, 0, 0.9, 1.1, 0.7, 2 + pair * 0.5,
                        friction: 0.3, vx: -1, angle: -0.1 * (pair + 1), angularZ: 0.2 * (pair - 1.5));
                }
                break;
            case PhysicsScene.RotatingRamp:
                Box(0, 0, 0, 80, 80, 1, 0, friction: 0.6, pitch: Math.PI / 6);
                for (int box = 0; box < 5; ++box)
                    Box((box - 2) * 4, 0, 1.3 + box * 0.2, 1, 0.8, 0.7, 0.5 + box * 0.75,
                        friction: 0.15 + box * 0.15, angle: box * 0.2, pitch: Math.PI / 6 + 0.15,
                        vy: -1, angularZ: 0.5 + box * 0.2);
                break;
        }
        return new(1.0 / 60, [0, 0, gravity], bodies.ToArray(), joints.ToArray(), events.ToArray(),
            joints.Select(joint => new SceneCollisionPair(joint.BodyA, joint.BodyB)).ToArray());

        void Box(double x, double y, double z, double sx, double sy, double sz, double mass,
            double friction = 0.5, double vx = 0, double angle = 0, double pitch = 0, double vz = 0,
            double vy = 0, double angularZ = 0)
        {
            double sineYaw = Math.Sin(angle / 2), cosineYaw = Math.Cos(angle / 2);
            double sinePitch = Math.Sin(pitch / 2), cosinePitch = Math.Cos(pitch / 2);
            bodies.Add(new([x, y, z], [cosineYaw * sinePitch, sineYaw * sinePitch, sineYaw * cosinePitch, cosineYaw * cosinePitch], [sx, sy, sz], mass,
                [mass * (sy * sy + sz * sz) / 12, mass * (sx * sx + sz * sz) / 12, mass * (sx * sx + sy * sy) / 12],
                friction, [vx, vy, vz], [0, 0, angularZ]));
        }
    }
}
