using System.Numerics;

namespace DefaultGame;

public static partial class PhysicsScenes
{
    private static PhysicsSceneData CreateStress(PhysicsScene scene) => ((int)scene switch
    {
        < 308 => CandidateScene(scene),
        < 318 => GeometryScene(scene),
        < 326 => ContactLifeScene(scene),
        < 334 => FrictionScene(scene),
        < 344 => SupportScene(scene),
        < 356 => JointScene(scene),
        < 364 => CoupledScene(scene),
        < 372 => IslandScene(scene),
        _ => InertiaScene(scene)
    }).Build();

    private static SceneBodies CandidateScene(PhysicsScene scene)
    {
        var world = new SceneBodies(0);
        switch (scene)
        {
            case PhysicsScene.SparseFlyers:
                for (int z = 0; z < 4; ++z)
                    for (int y = 0; y < 6; ++y)
                        for (int x = 0; x < 8; ++x)
                            world.Box(x * 5, y * 5, z * 5, .5, .7, .9, 1,
                                vx: .2 * (y % 2 == 0 ? 1 : -1), vy: .1 * (z - 1.5));
                break;
            case PhysicsScene.PlaneSeparatedCloud:
                for (int y = 0; y < 16; ++y)
                    for (int z = 0; z < 12; ++z)
                        world.Box((y % 2) * .1, (y - 7.5) * 2, z * 2, .5, .6, .7, 1, vx: .05);
                break;
            case PhysicsScene.LongParallelBars:
                for (int x = 0; x < 16; ++x)
                    for (int z = 0; z < 8; ++z)
                        world.Box(x * 1.2, 0, z * 1.5, .25, 18, .25, 1, vy: .1 * (x % 2 == 0 ? 1 : -1));
                break;
            case PhysicsScene.StaticMaze:
                world.Gravity = -10;
                world.Ground(80);
                for (int row = 0; row < 8; ++row)
                    for (int column = 0; column < 12; ++column)
                        world.Box(-22 + column * 4, -14 + row * 4, 1, 2.4, .3, 2, 0);
                for (int lane = 0; lane < 7; ++lane)
                    world.Box(-20, -12 + lane * 4, .5, .7, .7, 1, 1, friction: .05, vx: 3);
                break;
            case PhysicsScene.CounterflowLanes:
                for (int lane = 0; lane < 6; ++lane)
                    for (int item = 0; item < 12; ++item)
                    {
                        world.Box(-15 + item * 1.2, lane * 4, 0, .6, .6, .6, 1, vx: 2);
                        world.Box(15 - item * 1.2, lane * 4 + .9, 0, .6, .6, .6, 1, vx: -2);
                    }
                break;
            case PhysicsScene.OrthogonalStreams:
                for (int item = 0; item < 12; ++item)
                {
                    world.Box(-4 - item * 1.4, 0, 0, .7, .7, .7, 1, vx: 2);
                    world.Box(0, -4 - item * 1.4, .15, .7, .7, .7, 1, vy: 2);
                }
                break;
            case PhysicsScene.EnclosureFlyers:
                world.Box(-10.5, 0, 0, 1, 22, 22, 0);
                world.Box(10.5, 0, 0, 1, 22, 22, 0);
                world.Box(0, -10.5, 0, 20, 1, 22, 0);
                world.Box(0, 10.5, 0, 20, 1, 22, 0);
                world.Box(0, 0, -10.5, 20, 20, 1, 0);
                world.Box(0, 0, 10.5, 20, 20, 1, 0);
                for (int i = 0; i < 48; ++i)
                    world.Box(-6 + i % 4 * 4, -6 + i / 4 % 4 * 4, -4 + i / 16 * 4,
                        .6, .8, .7, 1, vx: i % 2 == 0 ? 1.4 : -1.4, vy: .3, vz: .2);
                break;
            case PhysicsScene.ClusterMigration:
                for (int column = 0; column < 8; ++column)
                    for (int row = 0; row < 5; ++row)
                        world.Box(-3 + column * 3, (row - 2) * 3, 0, .3, .3, 4, 0);
                for (int i = 0; i < 40; ++i)
                    world.Box(-12 - i / 5 * 1.1, -5.5 + i % 5 * 2.7, 0, .6, .6, .6, 1, vx: 3);
                break;
        }
        return world;
    }

    private static SceneBodies GeometryScene(PhysicsScene scene)
    {
        var world = new SceneBodies();
        switch (scene)
        {
            case PhysicsScene.FaceDrop:
                world.Ground();
                world.Box(0, 0, 1.4, 3, 2, .8, 4);
                break;
            case PhysicsScene.EdgeDrop:
                world.Ground();
                world.Box(0, 0, 1.2, 4, .5, .5, 2, tiltY: .35);
                break;
            case PhysicsScene.CornerDrop:
                world.Ground();
                world.Box(0, 0, 1.5, 1, 1, 1, 1, tiltX: .35, tiltY: .45, yaw: .2);
                break;
            case PhysicsScene.EdgeCross:
                world.Gravity = 0;
                world.Box(-.2, 0, -.8, 3, .25, .25, 2, vz: 1, tiltY: .15);
                world.Box(.2, 0, .8, 3, .25, .25, 2, vz: -1, yaw: 1.2, tiltX: .2);
                break;
            case PhysicsScene.CoplanarTiles:
                for (int x = -5; x <= 5; ++x)
                    for (int y = -1; y <= 1; ++y)
                        world.Box(x * 2, y * 2, -.5, 2, 2, 1, 0, friction: .2);
                world.Box(-7, 0, .4, 2.6, 1.4, .8, 3, friction: .2, vx: 3);
                break;
            case PhysicsScene.StaggeredTiles:
                for (int x = -5; x <= 5; ++x)
                    world.Box(x * 2, 0, -.5 + (x % 2 == 0 ? .015 : 0), 2, 4, 1, 0, friction: .15);
                world.Box(-7, 0, .43, 2.8, 1.5, .8, 3, friction: .15, vx: 2.5);
                break;
            case PhysicsScene.ThinPlates:
                world.Ground();
                for (int layer = 0; layer < 8; ++layer)
                    world.Box(0, 0, .15 + layer * .18, 3, 2, .12, 1, yaw: layer * .08);
                break;
            case PhysicsScene.NearlyParallelSlats:
                world.Gravity = 0;
                world.Box(0, 0, -.4, 6, .3, .3, 1, vz: .5, yaw: .0003);
                world.Box(0, 0, .4, 6, .3, .3, 1, vz: -.5, yaw: -.0003);
                break;
            case PhysicsScene.PocketLanding:
                world.Ground();
                world.Box(-1, 0, 1, .5, 4, 2, 0);
                world.Box(0, -1, 1, 4, .5, 2, 0);
                world.Box(0, 0, 2, 1, 1, 1, 2, vx: -.25, vy: -.25);
                break;
            case PhysicsScene.UnequalFaceLanding:
                world.Box(0, 0, -.5, 200, 200, 1, 0);
                world.Box(95, 94, .8, .4, .6, .3, .2, yaw: .3, vy: .4);
                break;
        }
        return world;
    }

    private static SceneBodies ContactLifeScene(PhysicsScene scene)
    {
        var world = new SceneBodies();
        switch (scene)
        {
            case PhysicsScene.RecontactHops:
                world.Ground();
                world.Box(0, 0, .5, 1, 1, 1, 1);
                foreach (int frame in new[] {60, 150, 240}) world.Impulse(frame, 1, .2, 0, 2);
                break;
            case PhysicsScene.SlidingOffEdge:
                world.Ground();
                world.Box(-2, 0, .4, 4, 4, .8, 0, friction: .05);
                world.Box(-2, 0, 1.3, 1, 1, 1, 1, friction: .05, vx: 2);
                break;
            case PhysicsScene.MovingFloor:
                world.Ground(30);
                for (int i = 0; i < 4; ++i) world.Box(0, 0, .5 + i, 1, 1, 1, 1);
                for (int frame = 90; frame < 180; ++frame) world.Move(frame, 0, (frame - 90) * .005, 0, -.5);
                break;
            case PhysicsScene.LoweredSupport:
                world.Ground();
                int platform = world.Box(0, 0, .5, 5, 5, 1, 0);
                for (int i = 0; i < 3; ++i) world.Box(0, 0, 1.5 + i, 1, 1, 1, 1);
                world.Move(120, platform, 0, 0, .2);
                break;
            case PhysicsScene.ContactExchange:
                world.Box(-2, 0, -.5, 4, 4, 1, 0, friction: .1);
                world.Box(2, 0, -.5, 4, 4, 1, 0, friction: .1);
                world.Box(-2, 0, .5, 3, 1, 1, 2, friction: .1, vx: 1.5);
                break;
            case PhysicsScene.RockingSlab:
                world.Ground();
                world.Box(0, 0, .55, 4, 2, .4, 3, tiltY: .15, wy: -.7);
                break;
            case PhysicsScene.RepeatedSideImpacts:
                world.Gravity = 0;
                world.Box(-2, 0, 0, .5, 6, 6, 0);
                world.Box(2, 0, 0, .5, 6, 6, 0);
                int slider = world.Box(0, 0, 0, .8, .8, .8, 1, vx: 1.5);
                world.Impulse(100, slider, -3, 0, 0);
                world.Impulse(220, slider, 3, 0, 0);
                break;
            case PhysicsScene.SeparationWave:
                world.Gravity = 0;
                for (int i = 0; i < 24; ++i)
                {
                    int body = world.Box(i, 0, 0, 1, 1, 1, 1);
                    world.Impulse(90 + i / 4 * 12, body, i < 12 ? -.5 : .5, 0, 0);
                }
                break;
        }
        return world;
    }

    private static SceneBodies FrictionScene(PhysicsScene scene)
    {
        var world = new SceneBodies();
        switch (scene)
        {
            case PhysicsScene.InclineHold:
                world.Box(0, 0, -.5, 20, 30, 1, 0, friction: .8, tiltX: .25);
                world.Box(0, 0, .6, 1, 1, 1, 1, friction: .8, tiltX: .25);
                break;
            case PhysicsScene.InclineSlip:
                world.Box(0, 0, -.5, 20, 60, 1, 0, friction: .15, tiltX: .4);
                world.Box(0, 0, .7, 1, 1, 1, 1, friction: .15, tiltX: .4);
                break;
            case PhysicsScene.CrossSlope:
                world.Box(0, 0, -.5, 60, 60, 1, 0, friction: .25, tiltX: .3);
                world.Box(-4, 0, .5, 1.6, 1, .8, 2, friction: .25, tiltX: .3, vx: 3, vy: 1);
                break;
            case PhysicsScene.SpinFriction:
                world.Ground();
                world.Box(0, 0, .5, 3, 2, 1, 4, friction: .4, wz: 2);
                break;
            case PhysicsScene.StackDrag:
                world.Ground();
                int bottom = world.Box(0, 0, .4, 3, 3, .8, 4, friction: .6);
                world.Box(0, 0, 1.3, 1, 1, 1, 1, friction: .6);
                for (int frame = 60; frame < 240; ++frame) world.Impulse(frame, bottom, .04, 0, 0);
                break;
            case PhysicsScene.FrictionSeam:
                world.Box(-10, 0, -.5, 20, 20, 1, 0, friction: .01);
                world.Box(10, 0, -.5, 20, 20, 1, 0, friction: .9);
                world.Box(-6, 0, .5, 3, 1, 1, 2, friction: .7, vx: 4);
                break;
            case PhysicsScene.StackTwist:
                world.Ground();
                world.Box(0, 0, .4, 3, 3, .8, 4, friction: .7, wz: 1);
                world.Box(0, 0, 1.3, 1.4, 1.4, 1, 1, friction: .7);
                world.Box(0, 0, 2.3, 1, 1, 1, 1, friction: .7);
                break;
            case PhysicsScene.ReversingFriction:
                world.Ground();
                int crate = world.Box(0, 0, .5, 2, 1, 1, 2, friction: .4);
                for (int frame = 30; frame < 300; ++frame)
                    world.Impulse(frame, crate, frame / 60 % 2 == 0 ? .09 : -.09, .02, 0);
                break;
        }
        return world;
    }

    private static SceneBodies SupportScene(PhysicsScene scene)
    {
        var world = new SceneBodies();
        switch (scene)
        {
            case PhysicsScene.OverhangBalance:
                world.Ground();
                world.Box(0, 0, 1, 1.5, 3, 2, 0);
                world.Box(.65, 0, 2.25, 3, 2, .5, 3);
                int load = world.Box(1.5, 0, 3, .8, .8, 1, 2);
                world.Impulse(90, load, .4, 0, 0);
                break;
            case PhysicsScene.ThreeLegTable:
                world.Ground();
                world.Box(-1.5, -1, 1, .6, .6, 2, 1);
                world.Box(1.5, -1, 1, .6, .6, 2, 1);
                world.Box(0, 1.5, 1, .6, .6, 2, 1);
                world.Box(0, 0, 2.2, 4, 4, .4, 3);
                world.Box(.5, .3, 2.9, 1, 1, 1, 2);
                break;
            case PhysicsScene.SplitFooting:
                world.Ground();
                int left = world.Box(-1.4, 0, .5, 1, 2, 1, 2, friction: .2);
                int right = world.Box(1.4, 0, .5, 1, 2, 1, 2, friction: .2);
                world.Box(0, 0, 1.25, 4, 2, .5, 4);
                world.Impulse(90, left, -.8, 0, 0);
                world.Impulse(90, right, .3, 0, 0);
                break;
            case PhysicsScene.StairLoad:
                world.Ground();
                for (int i = 0; i < 5; ++i)
                {
                    world.Box((i - 2) * 2, 0, .25 + i * .15, 2, 3, .5 + i * .3, 0);
                    world.Box((i - 2) * 2, 0, 1 + i * .3, 1.8, 2, 1, 1 + i);
                }
                world.Box(0, 0, 3.1, 8, 2, .4, 5, tiltY: -.12);
                break;
            case PhysicsScene.HollowTower:
                world.Ground();
                for (int layer = 0; layer < 4; ++layer)
                {
                    double height = layer * 1.8;
                    for (int x = -1; x <= 1; x += 2)
                        for (int y = -1; y <= 1; y += 2)
                            world.Box(x, y, height + .7, .5, .5, 1.4, 1);
                    world.Box(0, 0, height + 1.6, 3, 3, .4, 2);
                }
                break;
            case PhysicsScene.StackedBridges:
                world.Ground();
                for (int y = -1; y <= 1; y += 2)
                {
                    world.Box(-1.5, y * 1.5, .75, .7, .8, 1.5, 1);
                    world.Box(1.5, y * 1.5, .75, .7, .8, 1.5, 1);
                    world.Box(0, y * 1.5, 1.7, 4, 1, .4, 2);
                    world.Box(0, y * 1.5, 2.4, .7, .7, 1, 1);
                }
                world.Box(0, 0, 3.1, 1, 4, .4, 2);
                world.Box(0, .4, 3.8, .8, .8, 1, 2);
                break;
            case PhysicsScene.ForkSupport:
                world.Ground();
                world.Box(0, 0, .75, 1.6, 2, 1.5, 2);
                world.Box(0, 0, 1.75, 4, 2, .5, 4);
                for (int side = -1; side <= 1; side += 2)
                    for (int layer = 0; layer < 4; ++layer)
                        world.Box(side * 1.2, 0, 2.5 + layer, .8, .8, 1, 1);
                break;
            case PhysicsScene.SupportWithdrawal:
                world.Ground();
                int support = world.Box(-1.4, 0, 1, .8, 2, 2, 0);
                world.Box(1.4, 0, 1, .8, 2, 2, 0);
                world.Box(0, 0, 2.25, 4, 2, .5, 3);
                world.Box(0, 0, 3, .8, .8, 1, 1);
                for (int frame = 90; frame < 150; ++frame)
                    world.Move(frame, support, -1.4 - (frame - 90) * .05, 0, 1);
                break;
            case PhysicsScene.SideBrace:
                world.Ground();
                world.Box(1.05, 0, 3, .5, 4, 6, 0);
                for (int i = 0; i < 6; ++i) world.Box(0, 0, .5 + i, 1, 1, 1, 1);
                world.Impulse(90, 7, .8, 0, 0);
                break;
            case PhysicsScene.FloatingRaft:
                world.Gravity = 0;
                world.Box(0, 0, 0, 5, 5, .5, 4);
                for (int x = -1; x <= 1; ++x)
                    for (int y = -1; y <= 1; ++y)
                        world.Box(x * 1.3, y * 1.3, 1.2, .8, .8, .8, 1, vz: -.4);
                break;
        }
        return world;
    }

    private static SceneBodies JointScene(PhysicsScene scene)
    {
        var world = new SceneBodies();
        switch (scene)
        {
            case PhysicsScene.ClosedSquare:
            case PhysicsScene.ClosedPentagon:
            {
                int count = scene == PhysicsScene.ClosedSquare ? 4 : 5;
                var corners = Enumerable.Range(0, count).Select(i => Point(2 * Math.Cos(i * 2 * Math.PI / count),
                    0, 8 + 2 * Math.Sin(i * 2 * Math.PI / count))).ToArray();
                int[] rods = Enumerable.Range(0, count).Select(i => world.Rod(corners[i], corners[(i + 1) % count], .25, i == 0 ? 0 : 1)).ToArray();
                for (int i = 0; i < count; ++i) world.Link(rods[i], rods[(i + 1) % count], corners[(i + 1) % count]);
                world.Impulse(60, rods[count / 2], .3, .5, 0);
                break;
            }
            case PhysicsScene.DoubleAnchoredCable:
            {
                var points = Enumerable.Range(0, 17).Select(i => Point((i - 8) * .7, 0, 10 - 2 * Math.Sin(i * Math.PI / 16))).ToArray();
                int[] chain = world.Chain(points, .18, 1);
                int first = world.Box(points[0].X, 0, points[0].Z, .3, .3, .3, 0);
                int last = world.Box(points[^1].X, 0, points[^1].Z, .3, .3, .3, 0);
                world.Link(first, chain[0], points[0]); world.Link(last, chain[^1], points[^1]);
                world.Impulse(90, chain[7], 0, .7, 0);
                break;
            }
            case PhysicsScene.JointLadder:
            {
                var nodes = new int[2, 10];
                for (int side = 0; side < 2; ++side)
                    for (int row = 0; row < 10; ++row)
                        nodes[side, row] = world.Box(side * 1.5, 0, 12 - row, .3, .3, .3, row == 0 ? 0 : 1);
                for (int row = 0; row < 10; ++row)
                {
                    world.Link(nodes[0, row], nodes[1, row], Point(.75, 0, 12 - row));
                    if (row > 0)
                        for (int side = 0; side < 2; ++side)
                            world.Link(nodes[side, row - 1], nodes[side, row], Point(side * 1.5, 0, 12.5 - row));
                }
                world.Impulse(60, nodes[1, 9], .3, .8, 0);
                break;
            }
            case PhysicsScene.JointStar:
            {
                world.Gravity = 0;
                int center = world.Box(0, 0, 0, 1, 1, 1, 5);
                for (int arm = 0; arm < 12; ++arm)
                {
                    double angle = arm * Math.PI / 6;
                    Vector3 start = Point(.6 * Math.Cos(angle), .6 * Math.Sin(angle), 0);
                    Vector3 end = Point(2.5 * Math.Cos(angle), 2.5 * Math.Sin(angle), arm % 2 == 0 ? .3 : -.3);
                    int body = world.Rod(start, end, .15, 1);
                    world.Link(center, body, start);
                }
                world.Impulse(60, 1, 0, .7, .3);
                break;
            }
            case PhysicsScene.BinaryJointTree:
            {
                int root = world.Box(0, 0, 12, .4, .4, .4, 0);
                var parents = new List<(int Body, double X, double Z)> { (root, 0, 12) };
                for (int level = 0; level < 4; ++level)
                {
                    var children = new List<(int Body, double X, double Z)>();
                    foreach (var parent in parents)
                        for (int side = -1; side <= 1; side += 2)
                        {
                            double x = parent.X + side * Math.Pow(.5, level) * 3;
                            int body = world.Box(x, 0, parent.Z - 1.7, .3, .3, .6, 1);
                            world.Link(parent.Body, body, Point((parent.X + x) / 2, 0, parent.Z - .85));
                            children.Add((body, x, parent.Z - 1.7));
                        }
                    parents = children;
                }
                world.Impulse(90, world.Count - 1, .2, .5, 0);
                break;
            }
            case PhysicsScene.CombJointTree:
            {
                int[] spine = world.Chain(Enumerable.Range(0, 11).Select(i => Point(0, 0, 14 - i)).ToArray(), .2, 1);
                int anchor = world.Box(0, 0, 14, .3, .3, .3, 0); world.Link(anchor, spine[0], Point(0, 0, 14));
                for (int i = 0; i < spine.Length; ++i)
                {
                    Vector3 root = Point(0, 0, 13.5 - i), tip = Point(i % 2 == 0 ? 1.5 : -1.5, 0, 13.5 - i);
                    int branch = world.Rod(root, tip, .15, .5);
                    world.Link(spine[i], branch, root);
                }
                world.Impulse(60, world.Count - 1, 0, .6, 0);
                break;
            }
            case PhysicsScene.JointGrid:
            {
                var nodes = new int[5, 5];
                for (int x = 0; x < 5; ++x)
                    for (int z = 0; z < 5; ++z)
                        nodes[x, z] = world.Box(x * 1.2, 0, 10 - z * 1.2, .3, .3, .3, z == 0 ? 0 : 1);
                for (int x = 0; x < 5; ++x)
                    for (int z = 0; z < 5; ++z)
                    {
                        if (x > 0) world.Link(nodes[x - 1, z], nodes[x, z], Point((x - .5) * 1.2, 0, 10 - z * 1.2));
                        if (z > 0) world.Link(nodes[x, z - 1], nodes[x, z], Point(x * 1.2, 0, 10 - (z - .5) * 1.2));
                    }
                world.Impulse(60, nodes[2, 4], 0, .5, 0);
                break;
            }
            case PhysicsScene.AngularLockedTrain:
            case PhysicsScene.MixedJointLocks:
            {
                for (int i = 0; i < 8; ++i)
                {
                    world.Box(i * 1.1, 0, 10, 1, .4, .4, i == 0 ? 0 : 1);
                    if (i > 0) world.Link(i - 1, i, Point((i - .5) * 1.1, 0, 10),
                        scene == PhysicsScene.AngularLockedTrain || i % 2 == 0);
                }
                world.Impulse(90, 7, 0, .5, 0);
                break;
            }
            case PhysicsScene.CounterweightedJoint:
            {
                int center = world.Box(0, 0, 8, .4, .4, .4, 0);
                int beam = world.Rod(Point(-3, 0, 8), Point(1, 0, 8), .3, 1);
                world.Link(center, beam, Point(0, 0, 8));
                int left = world.Box(-3, 0, 7.5, .6, .6, .6, 1);
                int right = world.Box(1, 0, 7.5, .6, .6, .6, 12);
                world.Link(beam, left, Point(-3, 0, 8), true);
                world.Link(beam, right, Point(1, 0, 8), true);
                break;
            }
            case PhysicsScene.JointReleaseCascade:
            {
                int[] chain = world.Chain(Enumerable.Range(0, 13).Select(i => Point(0, 0, 16 - i)).ToArray(), .2, 1);
                int anchor = world.Box(0, 0, 16, .3, .3, .3, 0); world.Link(anchor, chain[0], Point(0, 0, 16));
                world.Remove(90, 11); world.Remove(150, 7); world.Remove(210, 3);
                break;
            }
        }
        return world;
    }

    private static SceneBodies CoupledScene(PhysicsScene scene)
    {
        var world = new SceneBodies();
        switch (scene)
        {
            case PhysicsScene.ChainOverPeg:
            {
                world.Ground();
                world.Box(0, 0, 2, 1.5, 2, 4, 0);
                var points = Enumerable.Range(0, 13).Select(i => Point((i - 6) * .7, 0, 4.6 + .15 * Math.Cos(i * Math.PI / 6))).ToArray();
                world.Chain(points, .2, 1);
                break;
            }
            case PhysicsScene.CableFloorLanding:
            {
                world.Ground();
                int[] chain = world.Chain(Enumerable.Range(0, 9).Select(i => Point(.1 * i, 0, 9 - i)).ToArray(), .2, 1);
                int anchor = world.Box(0, 0, 9, .3, .3, .3, 0);
                world.Link(anchor, chain[0], Point(0, 0, 9));
                world.Remove(60, 7);
                break;
            }
            case PhysicsScene.PendulumWall:
            {
                world.Box(2, 0, 3, .5, 6, 6, 0);
                int anchor = world.Box(0, 0, 6, .3, .3, .3, 0);
                int rod = world.Rod(Point(0, 0, 6), Point(0, 0, 2), .4, 2);
                world.Link(anchor, rod, Point(0, 0, 6)); world.Impulse(0, rod, 2, 0, 0);
                break;
            }
            case PhysicsScene.JointBridgeImpact:
            {
                world.Ground();
                int[] deck = world.Chain(Enumerable.Range(0, 9).Select(i => Point(i - 4, 0, 2)).ToArray(), .3, 2);
                int left = world.Box(-4, 0, 2, .4, .4, .4, 0), right = world.Box(4, 0, 2, .4, .4, .4, 0);
                world.Link(left, deck[0], Point(-4, 0, 2)); world.Link(right, deck[^1], Point(4, 0, 2));
                world.Box(.3, 0, 3.2, .7, .7, .7, 2);
                break;
            }
            case PhysicsScene.LinkedCratesSlide:
            {
                world.Ground();
                for (int i = 0; i < 5; ++i)
                {
                    int body = world.Box(-5 + i * 1.2, 0, .5, 1, 1, 1, 1, friction: .2 + i * .1);
                    if (i > 0) world.Link(body - 1, body, Point(-5 + (i - .5) * 1.2, 0, .5));
                }
                for (int frame = 30; frame < 240; ++frame) world.Impulse(frame, 5, .12, .02, 0);
                break;
            }
            case PhysicsScene.JointGateWedge:
            {
                int hinge = world.Box(0, 0, 1.5, .3, .3, 3, 0);
                int gate = world.Box(1.6, 0, 1.5, 3, .2, 2.5, 3);
                world.Link(hinge, gate, Point(.1, 0, .5)); world.Link(hinge, gate, Point(.1, 0, 2.5));
                world.Box(2.8, 1.2, 1.5, .5, .5, 3, 0);
                world.Impulse(30, gate, 0, 1, 0);
                break;
            }
            case PhysicsScene.BranchPile:
            {
                world.Ground();
                int root = world.Box(0, 0, 3, .4, .4, .4, 1);
                for (int branch = 0; branch < 3; ++branch)
                {
                    double angle = branch * Math.PI * 2 / 3;
                    var points = Enumerable.Range(0, 4).Select(i => Point(i * .6 * Math.Cos(angle), i * .6 * Math.Sin(angle), 3 - i * .75)).ToArray();
                    int[] chain = world.Chain(points, .2, 1);
                    world.Link(root, chain[0], points[0]);
                    for (int earlier = 0; earlier < branch; ++earlier) world.Exclude(2 + earlier * 3, chain[0]);
                }
                break;
            }
            case PhysicsScene.TetheredCollision:
            {
                world.Gravity = 0;
                int a = world.Box(-1.5, 0, 0, 1, 1, 1, 1, vx: 1);
                int b = world.Box(1.5, .2, 0, 1, 1, 1, 2, vx: -1);
                world.Distance(a, b, 0, 3.1);
                world.Impulse(150, a, -1.5, .5, 0); world.Impulse(150, b, 1.5, 0, 0);
                break;
            }
        }
        return world;
    }

    private static SceneBodies IslandScene(PhysicsScene scene)
    {
        var world = new SceneBodies();
        switch (scene)
        {
            case PhysicsScene.ManySingles:
                for (int x = 0; x < 12; ++x)
                    for (int y = 0; y < 12; ++y)
                    {
                        world.Box(x * 3, y * 3, -.3, 1.8, 1.8, .6, 0);
                        world.Box(x * 3, y * 3, .7, .8, .8, .8, 1);
                    }
                break;
            case PhysicsScene.ManyPairs:
                world.Gravity = 0;
                for (int x = 0; x < 12; ++x)
                    for (int y = 0; y < 8; ++y)
                    {
                        world.Box(x * 4, y * 4, -1, .8, .8, .8, 1, vz: .5);
                        world.Box(x * 4, y * 4, 1, .8, .8, .8, 2, vz: -.5);
                    }
                break;
            case PhysicsScene.UnevenIslands:
                world.Ground(140);
                for (int row = 0; row < 14; ++row)
                    for (int column = 0; column < 14 - row; ++column)
                        world.Box(column + row * .5, 0, row + .5, 1, 1, 1, 1);
                for (int island = 0; island < 30; ++island)
                    for (int layer = 0; layer < 2; ++layer)
                        world.Box(-20 + island % 10 * 4, 8 + island / 10 * 5, layer + .5, 1, 1, 1, 1);
                break;
            case PhysicsScene.TouchingIslands:
                world.Gravity = 0;
                for (int side = -1; side <= 1; side += 2)
                    for (int x = 0; x < 4; ++x)
                        for (int y = 0; y < 4; ++y)
                            world.Box(side * (4 + x), y, 0, 1, 1, 1, 1, vx: -side * .8);
                break;
            case PhysicsScene.SplittingIslands:
                world.Gravity = 0;
                for (int x = 0; x < 12; ++x)
                    for (int y = 0; y < 6; ++y)
                    {
                        int body = world.Box(x, y, 0, 1, 1, 1, 1);
                        world.Impulse(120, body, x < 6 ? -.5 : .5, y < 3 ? -.2 : .2, 0);
                    }
                break;
            case PhysicsScene.SharedAnchorChains:
            {
                int anchor = world.Box(0, 0, 9, 30, 30, .3, 0);
                for (int island = 0; island < 36; ++island)
                {
                    double x = (island % 6 - 2.5) * 3, y = (island / 6 - 2.5) * 3;
                    var points = Enumerable.Range(0, 6).Select(i => Point(x, y, 8.8 - i)).ToArray();
                    int[] chain = world.Chain(points, .2, 1);
                    world.Link(anchor, chain[0], points[0]);
                    world.Impulse(90, chain[^1], .2, .2, 0);
                }
                break;
            }
            case PhysicsScene.MixedConstraintIslands:
            {
                world.Ground(100);
                for (int island = 0; island < 24; ++island)
                    for (int layer = 0; layer < 5; ++layer)
                        world.Box((island % 6 - 2.5) * 4, island / 6 * 5, layer + .5, 1, 1, 1, 1);
                for (int island = 0; island < 8; ++island)
                {
                    double x = (island - 3.5) * 3;
                    int anchor = world.Box(x, -8, 8, .3, .3, .3, 0);
                    var points = Enumerable.Range(0, 5).Select(i => Point(x, -8, 8 - i)).ToArray();
                    int[] chain = world.Chain(points, .2, 1);
                    world.Link(anchor, chain[0], points[0]);
                    world.Impulse(60, chain[^1], .2, .3, 0);
                }
                break;
            }
            case PhysicsScene.MigratingImpact:
                world.Ground(100);
                for (int island = 0; island < 10; ++island)
                    for (int layer = 0; layer < 3; ++layer)
                        world.Box(-10 + island * 3, 0, layer + .5, 1, 1, 1, 1, friction: .1);
                world.Box(-15, 0, .5, 1, 1, 1, 20, friction: .02, vx: 6);
                break;
        }
        return world;
    }

    private static SceneBodies InertiaScene(PhysicsScene scene)
    {
        var world = new SceneBodies(0);
        switch (scene)
        {
            case PhysicsScene.FreeSymmetricSpin:
                for (int i = 0; i < 32; ++i)
                    world.Box(i % 8 * 4, i / 8 * 4, 0, 1, 1, 1, 1, wx: .4, wy: .7, wz: .9);
                break;
            case PhysicsScene.FreeAsymmetricTumble:
                for (int i = 0; i < 24; ++i)
                    world.Box(i % 6 * 5, i / 6 * 5, 0, .5, 1, 2, 2, wx: 1.2, wy: .6, wz: .3);
                break;
            case PhysicsScene.SlenderRodSpin:
                for (int i = 0; i < 12; ++i)
                    world.Box(i % 4 * 8, i / 4 * 8, 0, .08, .12, 5, 1, wx: .7, wy: .3, wz: 2);
                break;
            case PhysicsScene.FastRotatingImpact:
                world.Box(2.6, 0, 0, .5, 6, 6, 0);
                world.Box(0, 0, 0, .3, 4.8, .3, 2, wz: 3, vx: .2);
                break;
            case PhysicsScene.GyroscopicChain:
                for (int i = 0; i < 8; ++i)
                {
                    int body = world.Box(i * 1.4, 0, 0, 1.2, 1.2, .2, 1, wx: .8, wy: .2, wz: i % 2 == 0 ? 1 : -1);
                    if (i > 0) world.Link(body - 1, body, Point((i - .5) * 1.4, 0, 0));
                }
                break;
            case PhysicsScene.SpinTransfer:
                world.Box(0, 0, -.8, 2, 1, .6, 2, friction: .8, wz: 1.5, vz: .5);
                world.Box(0, 0, .8, 1.5, 1.5, .6, 3, friction: .8, vz: -.5);
                break;
        }
        return world;
    }

    private static Vector3 Point(double x, double y, double z) => new((float)x, (float)y, (float)z);

    private sealed class SceneBodies(double gravity = -10)
    {
        private readonly List<SceneBodyData> bodies = [];
        private readonly List<SceneJointData> joints = [];
        private readonly List<SceneInputEvent> events = [];
        private readonly HashSet<(int A, int B)> exclusions = [];
        private readonly List<SceneDistanceRange> distances = [];
        internal double Gravity = gravity;
        internal int Count => bodies.Count;

        internal int Ground(double width = 60) => Box(0, 0, -.5, width, width, 1, 0);

        internal int Box(double x, double y, double z, double sx, double sy, double sz, double mass,
            double friction = .5, double vx = 0, double vy = 0, double vz = 0,
            double yaw = 0, double tiltX = 0, double tiltY = 0, double wx = 0, double wy = 0, double wz = 0)
        {
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)yaw)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)tiltY)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)tiltX);
            return Add(Point(x, y, z), Point(sx, sy, sz), mass, friction, rotation, Point(vx, vy, vz), Point(wx, wy, wz));
        }

        internal int Rod(Vector3 first, Vector3 last, double width, double mass)
        {
            Vector3 direction = Vector3.Normalize(last - first);
            Quaternion rotation = direction.Z == -1 ? new Quaternion(1, 0, 0, 0)
                : Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitZ, direction), 1 + direction.Z));
            return Add((first + last) * .5f, new((float)width, (float)width, Vector3.Distance(first, last)), mass, .5, rotation, default, default);
        }

        private int Add(Vector3 center, Vector3 size, double mass, double friction,
            Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            double x = size.X, y = size.Y, z = size.Z;
            bodies.Add(new([center.X, center.Y, center.Z], [rotation.X, rotation.Y, rotation.Z, rotation.W],
                [x, y, z], mass, [mass * (y * y + z * z) / 12, mass * (x * x + z * z) / 12, mass * (x * x + y * y) / 12],
                friction, [velocity.X, velocity.Y, velocity.Z], [angularVelocity.X, angularVelocity.Y, angularVelocity.Z]));
            return bodies.Count - 1;
        }

        internal int[] Chain(Vector3[] points, double width, double mass)
        {
            int[] chain = new int[points.Length - 1];
            for (int i = 0; i < chain.Length; ++i)
            {
                chain[i] = Rod(points[i], points[i + 1], width, mass);
                if (i > 0) Link(chain[i - 1], chain[i], points[i]);
            }
            return chain;
        }

        internal void Link(int first, int last, Vector3 point, bool angularFixed = false)
        {
            joints.Add(new(first, last, Local(first, point), Local(last, point), angularFixed, joints.Count));
            Exclude(first, last);
        }

        private double[] Local(int body, Vector3 point)
        {
            SceneBodyData data = bodies[body];
            Quaternion rotation = new((float)data.Rotation[0], (float)data.Rotation[1], (float)data.Rotation[2], (float)data.Rotation[3]);
            Vector3 local = Vector3.Transform(point - Point(data.Position[0], data.Position[1], data.Position[2]), Quaternion.Conjugate(rotation));
            return [local.X, local.Y, local.Z];
        }

        internal void Exclude(int first, int last) => exclusions.Add((Math.Min(first, last), Math.Max(first, last)));
        internal void Impulse(int frame, int body, double x, double y, double z) => events.Add(new(frame, body, [x, y, z]));
        internal void Move(int frame, int body, double x, double y, double z) => events.Add(new(frame, body, Position: [x, y, z]));
        internal void Remove(int frame, int joint) => events.Add(new(frame, RemovedJoint: joint));
        internal void Distance(int first, int last, double minimum, double maximum)
            => distances.Add(new(first, last, [0, 0, 0], [0, 0, 0], minimum, maximum));

        internal PhysicsSceneData Build() => new(1.0 / 60, [0, 0, Gravity], bodies.ToArray(), joints.ToArray(), events.ToArray(),
            exclusions.Select(pair => new SceneCollisionPair(pair.A, pair.B)).ToArray(), distances.Count == 0 ? null : distances.ToArray());
    }
}
