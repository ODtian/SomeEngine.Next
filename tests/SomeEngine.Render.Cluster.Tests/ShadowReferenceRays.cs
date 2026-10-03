using System.Numerics;
using SharpGLTF.Schema2;

namespace SomeEngine.Render.Cluster.Tests;

/// <summary>Traces the original source triangles independently of shadow maps, Cluster decoding and GPU readback.</summary>
internal sealed class ShadowReferenceRays
{
    private readonly Triangle[] _triangles;
    private readonly List<BoundsNode> _bounds = [];
    private readonly ShadowReferenceScene.MeshPlacement[] _meshes;
    private readonly string _light;
    private readonly int[] _meshIndices;
    private readonly List<BoundsNode> _meshBounds = [];

    internal Vector3 LightDirection { get; }

    internal ShadowReferenceRays(string source, string light, Vector3? casterPosition = null, Vector3? lightDirection = null)
    {
        ModelRoot model = ModelRoot.Load(source);
        MeshPrimitive primitive = model.LogicalMeshes.Single().Primitives.Single();
        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        var normals = primitive.GetVertexAccessor("NORMAL").AsVector3Array();
        var indices = primitive.GetIndexAccessor().AsIndicesArray();
        _light = light;
        LightDirection = lightDirection ?? ShadowReferenceScene.Direction(light);
        _meshes = ShadowReferenceScene.Meshes(light, captured: true);
        if (casterPosition is Vector3 position)
        {
            int caster = Array.FindIndex(_meshes, mesh => !mesh.Receiver);
            _meshes[caster] = _meshes[caster] with { Position = position };
        }
        _meshIndices = Enumerable.Range(0, _meshes.Length).ToArray();
        _triangles = new Triangle[indices.Count / 3];
        for (int index = 0; index < indices.Count; index += 3)
        {
            int a = (int)indices[index], b = (int)indices[index + 1], c = (int)indices[index + 2];
            _triangles[index / 3] = new Triangle(positions[a], positions[b], positions[c], normals[a], normals[b], normals[c]);
        }
        BuildBounds(0, _triangles.Length);
        BuildMeshBounds(0, _meshes.Length);
    }

    internal bool ReceiverAt(int x, int y, out Vector3 position, out Vector3 normal)
    {
        int width = ShadowReferenceScene.ImageWidth(_light), height = ShadowReferenceScene.ImageHeight(_light);
        float tangent = MathF.Tan(ShadowReferenceScene.FieldOfView(_light) / 2);
        bool grid = ShadowReferenceScene.UsesDefaultGrid(_light);
        Vector3 direction = Vector3.Normalize(new Vector3(
            (2 * (x + .5f) / width - 1) * tangent * width / height * (grid ? -1 : 1),
            (1 - 2 * (y + .5f) / height) * tangent, grid ? 1 : -1));
        Vector3 origin = ShadowReferenceScene.CameraPosition(_light);
        float distance = float.PositiveInfinity;
        int meshIndex = -1;
        normal = default;
        TraceMeshes(0, origin, direction, firstHit: false, ref distance, ref meshIndex, ref normal);
        position = origin + direction * distance;
        return meshIndex >= 0 && _meshes[meshIndex].Receiver;
    }

    internal bool Occluded(Vector3 position, Vector3 normal, string light)
    {
        Vector3 origin = position + normal * .01f;
        Vector3 toLight = ShadowReferenceScene.IsPointLight(light) ? ShadowReferenceScene.PointLightPosition(light) - origin : -LightDirection;
        float distance = ShadowReferenceScene.IsPointLight(light) ? toLight.Length() : float.PositiveInfinity;
        Vector3 direction = Vector3.Normalize(toLight);
        int meshIndex = -1;
        Vector3 hitNormal = default;
        TraceMeshes(0, origin, direction, firstHit: true, ref distance, ref meshIndex, ref hitNormal);
        return meshIndex >= 0;
    }

    private int BuildMeshBounds(int start, int count)
    {
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        for (int index = start; index < start + count; index++)
        {
            var mesh = _meshes[_meshIndices[index]];
            minimum = Vector3.Min(minimum, _bounds[0].Minimum * mesh.Scale + mesh.Position);
            maximum = Vector3.Max(maximum, _bounds[0].Maximum * mesh.Scale + mesh.Position);
        }
        int node = _meshBounds.Count;
        _meshBounds.Add(new BoundsNode(minimum, maximum, start, count, -1, -1));
        if (count <= 4) return node;
        Vector3 extent = maximum - minimum;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(_meshIndices, start, count, Comparer<int>.Create((a, b) =>
            Coordinate(_meshes[a].Position, axis).CompareTo(Coordinate(_meshes[b].Position, axis))));
        int left = BuildMeshBounds(start, count / 2);
        int right = BuildMeshBounds(start + count / 2, count - count / 2);
        _meshBounds[node] = new BoundsNode(minimum, maximum, start, 0, left, right);
        return node;
    }

    private void TraceMeshes(int nodeIndex, Vector3 origin, Vector3 direction, bool firstHit,
        ref float distance, ref int meshIndex, ref Vector3 normal)
    {
        BoundsNode node = _meshBounds[nodeIndex];
        if (!IntersectsBounds(node.Minimum, node.Maximum, origin, direction, distance)) return;
        if (node.Count == 0)
        {
            TraceMeshes(node.Left, origin, direction, firstHit, ref distance, ref meshIndex, ref normal);
            if (firstHit && meshIndex >= 0) return;
            TraceMeshes(node.Right, origin, direction, firstHit, ref distance, ref meshIndex, ref normal);
            return;
        }
        for (int index = node.Start; index < node.Start + node.Count; index++)
        {
            int mesh = _meshIndices[index];
            if (!TraceMesh(_meshes[mesh], origin, direction, ref distance, out Vector3 hitNormal)) continue;
            meshIndex = mesh;
            normal = hitNormal;
            if (firstHit) return;
        }
    }

    private bool TraceMesh(ShadowReferenceScene.MeshPlacement mesh, Vector3 origin, Vector3 direction,
        ref float distance, out Vector3 normal)
    {
        Vector3 localOrigin = (origin - mesh.Position) / mesh.Scale;
        Vector3 localDirection = direction / mesh.Scale;
        int triangle = -1;
        Vector2 barycentric = default;
        Trace(0, localOrigin, localDirection, ref distance, ref triangle, ref barycentric);
        normal = triangle < 0 ? default : Vector3.Normalize((
            _triangles[triangle].NormalA * (1 - barycentric.X - barycentric.Y) +
            _triangles[triangle].NormalB * barycentric.X + _triangles[triangle].NormalC * barycentric.Y) / mesh.Scale);
        return triangle >= 0;
    }

    private int BuildBounds(int start, int count)
    {
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        for (int index = start; index < start + count; index++)
        {
            Triangle triangle = _triangles[index];
            minimum = Vector3.Min(minimum, Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C)));
            maximum = Vector3.Max(maximum, Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C)));
        }
        int node = _bounds.Count;
        _bounds.Add(new BoundsNode(minimum, maximum, start, count, -1, -1));
        if (count <= 8) return node;
        Vector3 extent = maximum - minimum;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(_triangles, start, count, Comparer<Triangle>.Create((a, b) =>
            Coordinate(a.A + a.B + a.C, axis).CompareTo(Coordinate(b.A + b.B + b.C, axis))));
        int left = BuildBounds(start, count / 2);
        int right = BuildBounds(start + count / 2, count - count / 2);
        _bounds[node] = new BoundsNode(minimum, maximum, start, 0, left, right);
        return node;
    }

    private void Trace(int nodeIndex, Vector3 origin, Vector3 direction, ref float distance,
        ref int triangleIndex, ref Vector2 barycentric)
    {
        BoundsNode node = _bounds[nodeIndex];
        if (!IntersectsBounds(node.Minimum, node.Maximum, origin, direction, distance)) return;
        if (node.Count == 0)
        {
            Trace(node.Left, origin, direction, ref distance, ref triangleIndex, ref barycentric);
            Trace(node.Right, origin, direction, ref distance, ref triangleIndex, ref barycentric);
            return;
        }
        for (int index = node.Start; index < node.Start + node.Count; index++)
        {
            Triangle triangle = _triangles[index];
            Vector3 edgeA = triangle.B - triangle.A, edgeB = triangle.C - triangle.A;
            Vector3 cross = Vector3.Cross(direction, edgeB);
            float determinant = Vector3.Dot(edgeA, cross);
            if (MathF.Abs(determinant) < 1e-10f) continue;
            Vector3 offset = origin - triangle.A;
            float u = Vector3.Dot(offset, cross) / determinant;
            if (u < 0 || u > 1) continue;
            Vector3 side = Vector3.Cross(offset, edgeA);
            float v = Vector3.Dot(direction, side) / determinant;
            if (v < 0 || u + v > 1) continue;
            float hit = Vector3.Dot(edgeB, side) / determinant;
            if (hit <= .0001f || hit >= distance) continue;
            distance = hit;
            triangleIndex = index;
            barycentric = new(u, v);
        }
    }

    private static bool IntersectsBounds(Vector3 minimum, Vector3 maximum,
        Vector3 origin, Vector3 direction, float distance)
    {
        float near = 0, far = distance;
        for (int axis = 0; axis < 3; axis++)
        {
            float component = Coordinate(direction, axis);
            float first = Coordinate(minimum - origin, axis), last = Coordinate(maximum - origin, axis);
            if (MathF.Abs(component) < 1e-10f)
            {
                if (first > 0 || last < 0) return false;
                continue;
            }
            first /= component;
            last /= component;
            near = Math.Max(near, Math.Min(first, last));
            far = Math.Min(far, Math.Max(first, last));
            if (near > far) return false;
        }
        return true;
    }

    private static float Coordinate(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C,
        Vector3 NormalA, Vector3 NormalB, Vector3 NormalC);
    private readonly record struct BoundsNode(Vector3 Minimum, Vector3 Maximum, int Start, int Count, int Left, int Right);
}
