using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public sealed class FootHull(int[] vertices, int bone)
{
    public int Bone { get; } = bone;
    private static readonly Vector3[] directions = (from x in Enumerable.Range(-2, 5)
        from y in Enumerable.Range(-2, 5) from z in Enumerable.Range(-2, 5)
        where Math.Abs(x) + Math.Abs(y) + Math.Abs(z) <= 3 && (x % 2 != 0 || y % 2 != 0 || z % 2 != 0)
        select Vector3.Normalize(new(x, y, z))).ToArray();
    private readonly Vector3[] normals = new Vector3[directions.Length];
    private readonly float[] distances = new float[directions.Length];
    private Vector3 minimum, maximum;
    private const float Clearance = .008f;

    public void Update(Vector3[] positions, Quaternion rotation)
    {
        minimum = new(float.PositiveInfinity); maximum = new(float.NegativeInfinity);
        for (int plane = 0; plane < normals.Length; plane++)
        {
            normals[plane] = Vector3.Transform(directions[plane], rotation);
            distances[plane] = float.NegativeInfinity;
        }
        foreach (int vertex in vertices)
        {
            Vector3 point = positions[vertex];
            minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
            for (int plane = 0; plane < normals.Length; plane++)
                distances[plane] = MathF.Max(distances[plane], Vector3.Dot(point, normals[plane]));
        }
        minimum -= new Vector3(Clearance); maximum += new Vector3(Clearance);
        minimum.Y = MathF.Max(ClothingContact.GroundHeight, minimum.Y);
    }

    public static FootHull Create(CharacterAsset character, int side, float height = .12f)
        => new(Enumerable.Range(0, character.Body.Positions.Length).Where(vertex => character.Body.Positions[vertex].Y < height
            && (character.Body.Positions[vertex].X > 0) == (side == 0)).ToArray(),
            Array.FindIndex(character.Bones, bone => bone.Name == (side == 0 ? "foot.L" : "foot.R")));

    public Vector3[] CreateRing(Vector3 center, Vector3 front, float side, int columns, bool tip)
    {
        List<Vector2> polygon = [new(-.5f, -.5f), new(.5f, -.5f), new(.5f, .5f), new(-.5f, .5f)];
        for (int plane = 0; plane < normals.Length; plane++)
        {
            Vector2 normal = new(normals[plane].X, Vector3.Dot(normals[plane], front));
            float distance = distances[plane] + Clearance - Vector3.Dot(normals[plane], center);
            List<Vector2> clipped = [];
            for (int edge = 0; edge < polygon.Count; edge++)
            {
                Vector2 a = polygon[edge], b = polygon[(edge + 1) % polygon.Count];
                float da = distance - Vector2.Dot(a, normal), db = distance - Vector2.Dot(b, normal);
                if (da >= 0) clipped.Add(a);
                if ((da >= 0) != (db >= 0)) clipped.Add(Vector2.Lerp(a, b, da / (da - db)));
            }
            polygon = clipped;
        }
        Vector2 origin = polygon.Aggregate(Vector2.Zero, (sum, point) => sum + point) / polygon.Count;
        Vector3 midpoint = center + Vector3.UnitX * origin.X + front * origin.Y;
        Vector3[] ring = new Vector3[columns];
        for (int column = 0; column < columns; column++)
        {
            float angle = column * MathF.Tau / columns;
            Vector3 direction = Vector3.UnitX * (MathF.Sin(angle) * side) + front * MathF.Cos(angle);
            float radius = float.PositiveInfinity;
            for (int plane = 0; plane < normals.Length; plane++)
            {
                float outward = Vector3.Dot(direction, normals[plane]);
                if (outward > .000001f) radius = MathF.Min(radius,
                    (distances[plane] + Clearance - Vector3.Dot(midpoint, normals[plane])) / outward);
            }
            ring[column] = tip ? midpoint : midpoint + direction * radius;
        }
        return ring;
    }

    public bool Overlaps(Vector3 low, Vector3 high, float padding = 0)
        => low.X < maximum.X + padding && low.Y < maximum.Y + padding && low.Z < maximum.Z + padding
            && high.X > minimum.X - padding && high.Y > minimum.Y - padding && high.Z > minimum.Z - padding;

    public int ClosestTrianglePlane(Vector3 a, Vector3 b, Vector3 c)
        => FindTrianglePlane(a, b, c, float.PositiveInfinity);

    private int FindTrianglePlane(Vector3 a, Vector3 b, Vector3 c, float separation)
    {
        float bestSeparation = float.NegativeInfinity;
        int closest = 0;
        for (int plane = 0; plane < normals.Length; plane++)
        {
            Vector3 normal = normals[plane];
            float gap = MathF.Min(Vector3.Dot(a, normal), MathF.Min(Vector3.Dot(b, normal), Vector3.Dot(c, normal))) - distances[plane];
            if (gap >= separation) return -1;
            if (gap > bestSeparation) { bestSeparation = gap; closest = plane; }
        }
        return closest;
    }

    public void ProjectTriangle(ref Vector3 a, ref Vector3 b, ref Vector3 c, int plane)
    {
        if (!Overlaps(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)))) return;
        ApplyPlane(ref a, ref b, ref c, plane, Clearance);
    }

    public void FitTriangle(ref Vector3 a, ref Vector3 b, ref Vector3 c)
    {
        if (!Overlaps(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)))) return;
        const float gap = .001f;
        int plane = FindTrianglePlane(a, b, c, gap);
        if (plane >= 0) ApplyPlane(ref a, ref b, ref c, plane, gap);
    }

    private void ApplyPlane(ref Vector3 a, ref Vector3 b, ref Vector3 c, int plane, float gap)
    {
        Vector3 outward = normals[plane];
        float distance = distances[plane] + gap;
        a += outward * MathF.Max(0, distance - Vector3.Dot(a, outward));
        b += outward * MathF.Max(0, distance - Vector3.Dot(b, outward));
        c += outward * MathF.Max(0, distance - Vector3.Dot(c, outward));
    }
}
