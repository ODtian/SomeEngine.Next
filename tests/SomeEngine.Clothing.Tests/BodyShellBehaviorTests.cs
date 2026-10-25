using System.Numerics;
using SomeEngine.Clothing.Demo;
using Xunit;
using Xunit.Abstractions;

namespace SomeEngine.Clothing.Tests;

public sealed class BodyShellBehaviorTests(ITestOutputHelper output)
{
    [Fact]
    public void CalfSectionsFollowSmoothSurfaceTangents()
    {
        CharacterAsset character = new();
        TransportShell shell = new(character, new BodyMesh(character));
        SurfaceTriangles surface = new(character.Body.Positions, character.Body.Triangles);
        float largestDeviation = 0;
        foreach (float height in new[] { .30f, .35f, .40f })
        {
            int[] ring = shell.Rings.MinBy(section => MathF.Abs(section.Average(vertex => shell.Points[vertex].Position.Y) - height))!;
            int count = ring.Length - 1;
            for (int column = 0; column < count; column++)
            {
                Vector3 before = shell.Points[ring[(column + count - 1) % count]].Position;
                Vector3 position = shell.Points[ring[column]].Position;
                Vector3 after = shell.Points[ring[(column + 1) % count]].Position;
                Vector3 tangent = Vector3.Normalize(after - before);
                SurfacePoint nearest = surface.Nearest(position);
                Vector3 normal = Vector3.Zero;
                for (int corner = 0; corner < 3; corner++)
                    normal += character.Body.Normals[character.Body.Triangles[nearest.Triangle * 3 + corner]] * nearest.Coordinates[corner];
                normal.Y = 0;
                normal = Vector3.Normalize(normal);
                largestDeviation = MathF.Max(largestDeviation, MathF.Asin(Math.Clamp(MathF.Abs(Vector3.Dot(tangent, normal)), 0, 1)));
            }
        }
        float degrees = largestDeviation * 180 / MathF.PI;
        output.WriteLine($"Largest geometric tangent deviation: {degrees:F3} degrees");
        Assert.InRange(degrees, 0, 3);
    }

    [Fact]
    public void EveryTransportRingIsOneProgressSection()
    {
        CharacterAsset character = new();
        TransportShell shell = new(character, new BodyMesh(character));
        float largestPlaneError = 0;
        foreach (int[] ring in shell.Rings)
        {
            float progress = shell.Points[ring[0]].Progress;
            Assert.All(ring, vertex => Assert.Equal(progress, shell.Points[vertex].Progress));
            if (ring.Any(vertex => shell.Points[vertex].Position.Y > .55f)) continue;
            Vector3 a = shell.Points[ring[0]].Position;
            Vector3 b = shell.Points[ring[ring.Length / 4]].Position;
            Vector3 c = shell.Points[ring[ring.Length / 2]].Position;
            if (a == b) continue;
            Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            largestPlaneError = MathF.Max(largestPlaneError, ring.Max(vertex => MathF.Abs(Vector3.Dot(shell.Points[vertex].Position - a, normal))));
        }
        output.WriteLine($"Progress section plane error: {largestPlaneError * 1000:F4} mm");
        Assert.InRange(largestPlaneError, 0, .00001f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransportShellEnclosesTheBodyDuringMotion(bool dualQuaternion)
    {
        CharacterAsset character = new();
        BodyMotion motion = new(character);
        BodyMesh body = new(character);
        TransportShell shell = new(character, body);
        int[][] faces = CharacterAsset.FindLargestSurface(character.Body.Faces
            .Where(face => face.All(vertex => character.Body.Positions[vertex].Y <= character.WaistHeight)).ToArray());
        int[] triangles = CharacterAsset.Triangulate(faces);
        int bodyPoint = triangles.Distinct().MinBy(vertex =>
            Vector3.DistanceSquared(character.Body.Positions[vertex], new(.08f, .6f, .04f)));
        SurfaceTriangles shellSurface = new(shell.Positions, shell.Triangles);
        SurfaceTriangles bodySurface = new(body.Positions, character.Body.Triangles);
        List<SurfaceCrossing> crossings = [];
        int[] waist = shell.BoundaryEdges.Distinct().ToArray();
        foreach ((float seconds, float running, float spread) in new[]
        {
            (0f, 0f, 0f), (.65f, .65f, .07f),
            (97f / 60, 97f / 119, .42f * 97 / 119), (1.9f, 1f, .42f),
        })
        {
            motion.Update(seconds, running, spread);
            body.Update(motion, dualQuaternion, 1f / 60);
            shell.Update(motion, dualQuaternion);
            shellSurface.Update(); bodySurface.Update();
            int count = 0;
            for (int face = 0; face < triangles.Length; face += 3)
            {
                crossings.Clear();
                shellSurface.FindCrossings(body.Positions[triangles[face]], body.Positions[triangles[face + 1]],
                    body.Positions[triangles[face + 2]], crossings);
                count += crossings.Count;
            }
            Vector3 waistCenter = waist.Aggregate(Vector3.Zero, (sum, vertex) => sum + shell.Positions[vertex]) / waist.Length;
            Vector3 sample = body.Positions[bodyPoint];
            double solidAngle = 0;
            void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
            {
                a -= sample; b -= sample; c -= sample;
                double numerator = Vector3.Dot(a, Vector3.Cross(b, c));
                double denominator = (double)a.Length() * b.Length() * c.Length()
                    + Vector3.Dot(a, b) * (double)c.Length() + Vector3.Dot(b, c) * (double)a.Length()
                    + Vector3.Dot(c, a) * (double)b.Length();
                solidAngle += 2 * Math.Atan2(numerator, denominator);
            }
            for (int face = 0; face < shell.Triangles.Length; face += 3)
                AddTriangle(shell.Positions[shell.Triangles[face]], shell.Positions[shell.Triangles[face + 1]],
                    shell.Positions[shell.Triangles[face + 2]]);
            for (int edge = 0; edge < shell.BoundaryEdges.Length; edge += 2)
                AddTriangle(waistCenter, shell.Positions[shell.BoundaryEdges[edge + 1]], shell.Positions[shell.BoundaryEdges[edge]]);
            double winding = solidAngle / (4 * Math.PI);
            output.WriteLine($"Time {seconds:F3}: body intersections {count}; winding {winding:F6}");
            Assert.Equal(0, count);
            Assert.InRange(winding, .9999, 1.0001);
            float maximumGap = shell.Points.Select((point, vertex) => (point, vertex))
                .Where(pair => !pair.point.ToeContour)
                .Max(pair => MathF.Sqrt(bodySurface.Nearest(shell.Positions[pair.vertex]).DistanceSquared));
            output.WriteLine($"Maximum body clearance: {maximumGap * 1000:F4} mm");
            Assert.InRange(maximumGap, .00099f, .00101f);
            if (seconds == 0)
            {
                float[] gaps = shell.Points.Select((point, vertex) => (point, vertex))
                    .Where(pair => pair.vertex % 64 == 0 && pair.point.Position.Y > .25f)
                    .Select(pair => MathF.Sqrt(bodySurface.Nearest(shell.Positions[pair.vertex]).DistanceSquared)).ToArray();
                output.WriteLine($"Reference clearance: {gaps.Min() * 1000:F4}–{gaps.Max() * 1000:F4} mm");
                Assert.InRange(gaps.Min(), .00099f, .00101f);
                Assert.InRange(gaps.Max(), .00099f, .00101f);
            }
        }
    }
}
