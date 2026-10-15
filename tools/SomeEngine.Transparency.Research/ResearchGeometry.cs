using System.Numerics;

namespace SomeEngine.Transparency.Research;

public static class ResearchGeometry
{
    public static TriangleHierarchy CreateShadedSpheres(int models, float alpha, int width, int height, float depthSpacing = .025f)
    {
        ProjectedTriangle[][] geometry = CreateSphereModels(models, alpha, width, height, depthSpacing);
        Vector3 light = Vector3.Normalize(new(.4f, .8f, -.4f));
        return new(geometry.Select(group => group.Select(triangle =>
            {
                Vector3 a = new(triangle.A.X, triangle.A.Y, triangle.A.Z);
                Vector3 b = new(triangle.B.X, triangle.B.Y, triangle.B.Z);
                Vector3 c = new(triangle.C.X, triangle.C.Y, triangle.C.Z);
                Vector3 scale = new(.42f, .42f, .03f);
                Vector3 normal = Vector3.Normalize(Vector3.Cross((b - a) / scale, (c - a) / scale));
                float lighting = .15f + .85f * MathF.Max(Vector3.Dot(normal, light), 0);
                Vector4 color = triangle.Color;
                return triangle with { Color = new(color.X * lighting, color.Y * lighting, color.Z * lighting, color.W) };
            }).ToArray()).ToArray());
    }

    public static TriangleHierarchy CreateSpheres(int models, float alpha, int width, int height, float depthSpacing = .075f)
        => new(CreateSphereModels(models, alpha, width, height, depthSpacing));

    private static ProjectedTriangle[][] CreateSphereModels(int models, float alpha, int width, int height, float depthSpacing)
    {
        const int latitudeBands = 8, longitudeBands = 12;
        var random = new Random(314159);
        var source = new ProjectedTriangle[models][];
        uint triangleId = 0;
        for (int model = 0; model < models; model++)
        {
            Vector2 center = new(.12f * MathF.Sin(model), .12f * MathF.Cos(model));
            float centerDepth = .15f + model * depthSpacing;
            Vector4 color = new((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), alpha);
            var triangles = new List<ProjectedTriangle>();
            for (int latitude = 0; latitude < latitudeBands; latitude++)
            for (int longitude = 0; longitude < longitudeBands; longitude++)
            {
                Vector4 a = Point(latitude, longitude), b = Point(latitude, longitude + 1);
                Vector4 c = Point(latitude + 1, longitude + 1), d = Point(latitude + 1, longitude);
                if (latitude > 0) triangles.Add(new(a, b, c, color, triangleId++));
                if (latitude + 1 < latitudeBands) triangles.Add(new(a, c, d, color, triangleId++));
            }
            source[model] = triangles.ToArray();

            Vector4 Point(int latitude, int longitude)
            {
                float theta = latitude * MathF.PI / latitudeBands;
                float phi = longitude * 2 * MathF.PI / longitudeBands;
                float x = center.X + .42f * MathF.Sin(theta) * MathF.Cos(phi);
                float y = center.Y + .42f * MathF.Cos(theta);
                float depth = centerDepth + .03f * MathF.Sin(theta) * MathF.Sin(phi);
                return new(MathF.Round((x + 1) * width * 128) / (width * 128) - 1,
                    1 - MathF.Round((1 - y) * height * 128) / (height * 128), depth, 1);
            }
        }
        return source;
    }

    public static TriangleHierarchy CreatePanels(int panels, float alpha, int width = 128, int height = 128)
    {
        var random = new Random(314159);
        var models = new ProjectedTriangle[panels][];
        for (int panel = 0; panel < panels; panel++)
        {
            float depth = .08f + .8f * (panel + .5f) / panels;
            float angle = panel * .173f;
            float size = .72f;
            Vector2 offset = new(.1f * MathF.Sin(angle * 2), .1f * MathF.Cos(angle * 3));
            Vector4 color = new((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), alpha);
            Vector4 a = Point(-size, -size), b = Point(size, -size), c = Point(size, size), d = Point(-size, size);
            models[panel] = [new(a, b, c, color, (uint)(panel * 2)), new(a, c, d, color, (uint)(panel * 2 + 1))];
            Vector4 Point(float x, float y)
            {
                float projectedX = x * MathF.Cos(angle) - y * MathF.Sin(angle) + offset.X;
                float projectedY = x * MathF.Sin(angle) + y * MathF.Cos(angle) + offset.Y;
                return new(MathF.Round((projectedX + 1) * width * 128) / (width * 128) - 1,
                    1 - MathF.Round((1 - projectedY) * height * 128) / (height * 128), depth, 1);
            }
        }
        return new(models);
    }
}
