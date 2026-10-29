using System.Numerics;
using SomeEngine.Physics;

namespace DefaultGame;

internal static class PhysicsSiloScenes
{
    internal const int StaticBodies = 9;
    internal const float Radius = .2f;
    internal const int OpenStep = 180;
    internal static readonly ConvexHullData Hull = CreateHull();

    internal static PhysicsSceneDescription Describe(int particles) =>
        new($"silo-{particles}", $"{particles:N0} mixed-shape silo", 3, particles == 500000 ? 409 : 408,
            "Mixed shapes", "Gate opens at step 180", "Drop, pile and discharge");

    internal static PhysicsSceneData Create(int particles)
    {
        int columns = (int)Math.Ceiling(Math.Cbrt(particles));
        int rows = columns;
        double half = Math.Max(3, columns * .23 + 1), height = 2 + Math.Ceiling(particles / (double)(columns * rows)) * .46;
        const double opening = 2;
        var bodies = new SceneBodyData[particles + StaticBodies];
        bodies[0] = Box(0, 0, -.1, opening * 2, opening * 2, .2);
        bodies[1] = Box(-(half + opening) * .5, 0, -.1, half - opening, half * 2, .2);
        bodies[2] = Box((half + opening) * .5, 0, -.1, half - opening, half * 2, .2);
        bodies[3] = Box(0, -(half + opening) * .5, -.1, opening * 2, half - opening, .2);
        bodies[4] = Box(0, (half + opening) * .5, -.1, opening * 2, half - opening, .2);
        bodies[5] = Box(-half - .1, 0, height * .5, .2, half * 2 + .4, height);
        bodies[6] = Box(half + .1, 0, height * .5, .2, half * 2 + .4, height);
        bodies[7] = Box(0, -half - .1, height * .5, half * 2, .2, height);
        bodies[8] = Box(0, half + .1, height * .5, half * 2, .2, height);
        for (int index = 0; index < particles; index++)
        {
            int column = index % columns, row = index / columns % rows, layer = index / (columns * rows);
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, index * .37f);
            double inertia = (index % 3) switch { 0 => .4 * Radius * Radius, 1 => 2d * Radius * Radius / 3, _ => .2 * Radius * Radius };
            bodies[StaticBodies + index] = new(
                [(column - (columns - 1) * .5) * .46, (row - (rows - 1) * .5) * .46, 1 + layer * .46],
                [rotation.X, rotation.Y, rotation.Z, rotation.W], [Radius * 2, Radius * 2, Radius * 2], 1,
                [inertia, inertia, inertia], .5, [0, 0, 0], [0, 0, 0]);
        }
        return new(1d / 60, [0, 0, -10], bodies, [],
            [new(OpenStep, 0, Position: [half * 2 + opening * 2, 0, -.1])], [],
            Materials: new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
            ContactDetectionInterval: 1, ContactHistoryEnabled: true);
    }

    private static SceneBodyData Box(double x, double y, double z, double width, double depth, double height) =>
        new([x, y, z], [0, 0, 0, 1], [width, depth, height], 0, [0, 0, 0], .5, [0, 0, 0], [0, 0, 0]);

    private static ConvexHullData CreateHull()
    {
        Vector3[] vertices = [new(Radius, 0, 0), new(-Radius, 0, 0), new(0, Radius, 0),
            new(0, -Radius, 0), new(0, 0, Radius), new(0, 0, -Radius)];
        var faces = new HullFace[8];
        var indices = new int[24];
        int face = 0;
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            int first = x > 0 ? 0 : 1, second = y > 0 ? 2 : 3, third = z > 0 ? 4 : 5;
            if (x * y * z < 0) (second, third) = (third, second);
            indices[face * 3] = first; indices[face * 3 + 1] = second; indices[face * 3 + 2] = third;
            faces[face] = new(Vector3.Normalize(new(x, y, z)), face * 3, 3);
            face++;
        }
        return new(vertices, faces, indices);
    }
}
