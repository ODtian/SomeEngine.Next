using SomeEngine.ECS;
using SomeEngine.Physics;

namespace DefaultGame;

public static class PhysicsScaleScenes
{
    public static IReadOnlyList<PhysicsSceneDescription> All { get; } = Array.AsReadOnly<PhysicsSceneDescription>(
    [
        new("bricks-110000", "110,000 连续砖结构", 3, 400, "连续结构", "第 120 步撞击", "承重、撞击、坍塌与整步成本"),
        new("bricks-510000", "510,000 连续砖结构", 3, 401, "连续结构", "第 120 步撞击", "承重、撞击、坍塌与整步成本"),
        new("pyramids-88200", "88,200 体金字塔阵列", 3, 402, "独立结构", "第 120 步逐结构撞击", "岛合并、坍塌与整步成本"),
        new("pyramids-176400", "176,400 体金字塔阵列", 3, 403, "独立结构", "第 120 步逐结构撞击", "岛合并、坍塌与整步成本"),
        new("joint-grid-100000", "100,000 节点关节网", 3, 404, "大型连接网络", "第 120 步面外冲量", "大连通网络、自接触与整步成本"),
    ]);

    public static PhysicsSceneSimulation CreateCpu<TCollision>(World world, PhysicsSceneDescription description,
        TCollision collision, PhysicsSceneSettings settings)
        where TCollision : struct, ICollisionNarrowPhase<BoxGeometry, BoxGeometry> =>
        PhysicsScenes.CreateCpu(world, description, Create(description), collision, settings);

    private static PhysicsSceneData Create(PhysicsSceneDescription description) => description.Scene switch
    {
        400 => CreateBricks(11),
        401 => CreateBricks(51),
        402 => CreatePyramids(20, 21),
        403 => CreatePyramids(28, 30),
        404 => CreateJointGrid(),
        _ => throw new ArgumentOutOfRangeException(nameof(description)),
    };

    private static PhysicsSceneData CreateJointGrid()
    {
        const int columns = 400, rows = 250;
        const double spacing = 1.2;
        var bodies = new SceneBodyData[columns * rows + 1];
        bodies[0] = Box(0, 0, -.5, 500, 10, 1, 0);
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                bodies[1 + row * columns + column] = Box((column - 199.5) * spacing, 0,
                    300 - row * spacing, .3, .3, .3, row == 0 ? 0 : 1);
        int connectionCount = rows * (columns - 1) + (rows - 1) * columns + (rows - 1) * (columns - 1);
        var joints = new SceneJointData[connectionCount];
        var exclusions = new SceneCollisionPair[connectionCount];
        int index = 0;
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                int body = 1 + row * columns + column;
                if (column > 0) Connect(body - 1, body, [.6, 0, 0], [-.6, 0, 0]);
                if (row > 0) Connect(body - columns, body, [0, 0, -.6], [0, 0, .6]);
                if (row > 0 && column > 0) Connect(body - columns - 1, body, [.6, 0, -.6], [-.6, 0, .6]);
            }
        return new PhysicsSceneData(1.0 / 60, [0, 0, -10], bodies, joints,
            [new(120, 1 + (rows - 1) * columns + columns / 2, [0, 12, 0])], exclusions,
            Materials: new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
            ContactDetectionInterval: 1, ContactHistoryEnabled: true);

        void Connect(int first, int second, double[] firstAnchor, double[] secondAnchor)
        {
            joints[index] = new(first, second, firstAnchor, secondAnchor, false, index);
            exclusions[index] = new(first, second);
            index++;
        }
    }

    private static PhysicsSceneData CreateBricks(int layers)
    {
        const int columns = 100, rows = 100;
        var bodies = new SceneBodyData[columns * rows * layers + 2];
        bodies[0] = Box(0, 0, -.5, 124, 54, 1, 0);
        int index = 1;
        for (int layer = 0; layer < layers; layer++)
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                    bodies[index++] = Box(column - 49.5 + (layer % 2) * .5, (row - 49.5) * .5,
                        layer * .5 + .25, 1, .5, .5, 1);
        bodies[index] = Box(-60, 0, 1, 2, 2, 2, 500);
        return new PhysicsSceneData(1.0 / 60, [0, 0, -10], bodies, [],
            [new(120, index, [25, 0, 0])], [], Materials: new(double.PositiveInfinity,
                double.PositiveInfinity, double.PositiveInfinity), ContactDetectionInterval: 1,
            ContactHistoryEnabled: true);
    }

    private static PhysicsSceneData CreatePyramids(int columns, int rows)
    {
        const int layers = 20, bricksPerStructure = 210;
        int structures = columns * rows;
        var bodies = new SceneBodyData[structures * (bricksPerStructure + 1) + 1];
        bodies[0] = Box(0, 0, -.5, (columns - 1) * 24 + 30, (rows - 1) * 6 + 6, 1, 0);
        int index = 1;
        // Each structure keeps the source BepuPyramids 20-row unit-box layout.
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                double centerX = (column - (columns - 1) * .5) * 24;
                double centerY = (row - (rows - 1) * .5) * 6;
                for (int layer = 0; layer < layers; layer++)
                    for (int brick = 0; brick < layers - layer; brick++)
                        bodies[index++] = Box(centerX - (layers - layer) * .5 + brick, centerY,
                            layer + .5, 1, 1, 1, 1);
            }
        var events = new SceneInputEvent[structures];
        for (int structure = 0; structure < structures; structure++)
        {
            double centerX = (structure % columns - (columns - 1) * .5) * 24;
            double centerY = (structure / columns - (rows - 1) * .5) * 6;
            bodies[index] = Box(centerX - 13, centerY, 1, 2, 2, 2, 500);
            events[structure] = new(120, index++, [25, 0, 0]);
        }
        return new PhysicsSceneData(1.0 / 60, [0, 0, -10], bodies, [], events, [],
            Materials: new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
            ContactDetectionInterval: 1, ContactHistoryEnabled: true);
    }

    private static SceneBodyData Box(double x, double y, double z, double width, double depth, double height, double mass) =>
        new([x, y, z], [0, 0, 0, 1], [width, depth, height], mass,
            [mass * (depth * depth + height * height) / 12, mass * (width * width + height * height) / 12,
                mass * (width * width + depth * depth) / 12], .6, [0, 0, 0], [0, 0, 0]);
}
