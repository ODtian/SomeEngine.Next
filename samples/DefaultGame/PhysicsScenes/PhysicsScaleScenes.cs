using SomeEngine.ECS;
using SomeEngine.Physics;

namespace DefaultGame;

public static class PhysicsScaleScenes
{
    public static IReadOnlyList<PhysicsSceneDescription> All { get; } = Array.AsReadOnly<PhysicsSceneDescription>(
    [
        new("bricks-110000", "110,000 连续砖结构", 3, 400, "连续结构", "第 120 步撞击", "承重、撞击、坍塌与整步成本"),
    ]);

    public static PhysicsSceneSimulation CreateCpu<TCollision>(World world, PhysicsSceneDescription description,
        TCollision collision, PhysicsSceneSettings settings)
        where TCollision : struct, ICollisionNarrowPhase<BoxGeometry, BoxGeometry> =>
        PhysicsScenes.CreateCpu(world, description, Create(description), collision, settings);

    private static PhysicsSceneData Create(PhysicsSceneDescription description) => description.Scene switch
    {
        400 => CreateBricks(11),
        _ => throw new ArgumentOutOfRangeException(nameof(description)),
    };

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

    private static SceneBodyData Box(double x, double y, double z, double width, double depth, double height, double mass) =>
        new([x, y, z], [0, 0, 0, 1], [width, depth, height], mass,
            [mass * (depth * depth + height * height) / 12, mass * (width * width + height * height) / 12,
                mass * (width * width + depth * depth) / 12], .6, [0, 0, 0], [0, 0, 0]);
}
