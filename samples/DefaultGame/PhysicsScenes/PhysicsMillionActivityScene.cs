namespace DefaultGame;

public static partial class PhysicsScaleScenes
{
    private static PhysicsSceneData CreateMillionActivity()
    {
        const int columns = 100, rows = 500, layers = 20, bodiesPerLayer = columns * rows;
        const double spacing = .75, height = .5, layerSpacing = .55;
        var bodies = new SceneBodyData[columns * rows * layers + 1];
        bodies[0] = Box(0, 0, -.5, columns * spacing + 2, rows * spacing + 2, 1, 0);
        int index = 1;
        for (int layer = 0; layer < layers; layer++)
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                    bodies[index++] = Box((column - (columns - 1) * .5) * spacing,
                        (row - (rows - 1) * .5) * spacing, .3 + layer * layerSpacing, .5, .5, height, 1);
        int impacted = 1 + (layers - 1) * bodiesPerLayer + (rows / 2) * columns + columns / 2;
        return new PhysicsSceneData(1d / 60, [0, 0, -10], bodies, [],
            [new(180, impacted, [2, 0, 0]), new(360, Body: 0, Position: [.025, 0, -.5])], [],
            Materials: new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
            ContactDetectionInterval: 1, ContactHistoryEnabled: true);
    }
}
