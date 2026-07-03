using System;
using System.Collections.Generic;
using System.Numerics;
using SomeEngine.Assets.Data;

namespace SomeEngine.Assets.Importers;

public static class PrimitiveMeshGenerator
{
    private static readonly float X = 0.525731112119133606f;
    private static readonly float Z = 0.850650808352039932f;
    private static readonly float N = 0f;

    private static readonly Vector3[] BaseVertices =
    {
        new(-X, N, Z),
        new(X, N, Z),
        new(-X, N, -Z),
        new(X, N, -Z),
        new(N, Z, X),
        new(N, Z, -X),
        new(N, -Z, X),
        new(N, -Z, -X),
        new(Z, X, N),
        new(-Z, X, N),
        new(Z, -X, N),
        new(-Z, -X, N),
    };

    private static readonly uint[] BaseIndices =
    [
        0,
        1,
        4,
        0,
        4,
        9,
        9,
        4,
        5,
        4,
        8,
        5,
        4,
        1,
        8,
        8,
        1,
        10,
        8,
        10,
        3,
        5,
        8,
        3,
        5,
        3,
        2,
        2,
        3,
        7,
        7,
        3,
        10,
        7,
        10,
        6,
        7,
        6,
        11,
        11,
        6,
        0,
        0,
        6,
        1,
        6,
        10,
        1,
        9,
        11,
        0,
        9,
        2,
        11,
        9,
        5,
        2,
        7,
        11,
        2,
    ];

    public static (
        Vector3[] Vertices,
        uint[] Indices,
        List<RawAttribute> Attributes
    ) CreateIcoSphere(int subdivision)
    {
        var vertices = new List<Vector3>(BaseVertices);
        List<uint> indices = Subdivide(BaseIndices, vertices, subdivision);
        List<RawAttribute> attributes = CreateSphereAttributes(vertices);

        return (vertices.ToArray(), indices.ToArray(), attributes);
    }

    private static List<uint> Subdivide(
        IReadOnlyList<uint> baseIndices,
        List<Vector3> vertices,
        int subdivision)
    {
        var indices = new List<uint>(baseIndices);
        var cache = new Dictionary<(uint, uint), uint>();

        for (int i = 0; i < subdivision; i++)
        {
            var newIndices = new List<uint>();
            for (int j = 0; j < indices.Count; j += 3)
            {
                uint a = indices[j];
                uint b = indices[j + 1];
                uint c = indices[j + 2];

                uint ab = GetMidPoint(a, b, vertices, cache);
                uint bc = GetMidPoint(b, c, vertices, cache);
                uint ca = GetMidPoint(c, a, vertices, cache);
                AddSubdividedTriangle(newIndices, a, b, c, ab, bc, ca);
            }

            indices = newIndices;
        }

        return indices;
    }

    private static void AddSubdividedTriangle(
        List<uint> indices,
        uint a,
        uint b,
        uint c,
        uint ab,
        uint bc,
        uint ca)
    {
        indices.Add(a);
        indices.Add(ab);
        indices.Add(ca);
        indices.Add(b);
        indices.Add(bc);
        indices.Add(ab);
        indices.Add(c);
        indices.Add(ca);
        indices.Add(bc);
        indices.Add(ab);
        indices.Add(bc);
        indices.Add(ca);
    }

    private static List<RawAttribute> CreateSphereAttributes(IReadOnlyList<Vector3> vertices)
    {
        float[] normals = new float[vertices.Count * 3];
        float[] tangents = new float[vertices.Count * 4]; // Vec4: xyz + w (handedness)
        float[] uvs = new float[vertices.Count * 2];

        for (int i = 0; i < vertices.Count; i++)
        {
            var p = vertices[i];
            normals[i * 3 + 0] = p.X;
            normals[i * 3 + 1] = p.Y;
            normals[i * 3 + 2] = p.Z;

            WriteUv(uvs, i, p);
            Vector3 tangent = TangentFor(p);
            tangents[i * 4 + 0] = tangent.X;
            tangents[i * 4 + 1] = tangent.Y;
            tangents[i * 4 + 2] = tangent.Z;
            tangents[i * 4 + 3] = 1.0f; // handedness
        }

        // Canonical SoA stream order: NORMAL → TANGENT → TEXCOORD → COLOR → ...
        var attributes = new List<RawAttribute>
        {
            new("NORMAL", normals, 3, SomeEngine.Assets.Data.ValueType.Int8, 3, true),
            new("TANGENT", tangents, 4, SomeEngine.Assets.Data.ValueType.Int8, 4, true),
            new("TEXCOORD_0", uvs, 2, SomeEngine.Assets.Data.ValueType.Float16, 2, false),
        };
        return attributes;
    }

    private static void WriteUv(float[] uvs, int index, Vector3 p)
    {
        float u = 0.5f + (float)(Math.Atan2(p.Z, p.X) / (2 * Math.PI));
        float v = 0.5f - (float)(Math.Asin(p.Y) / Math.PI);
        uvs[index * 2 + 0] = u;
        uvs[index * 2 + 1] = v;
    }

    private static Vector3 TangentFor(Vector3 position)
    {
        Vector3 tangent = Vector3.Cross(Vector3.UnitY, position);
        float tangentLen = tangent.Length();
        return tangentLen < 1e-6f
            ? Vector3.UnitX
            : tangent / tangentLen;
    }

    private static uint GetMidPoint(
        uint p1,
        uint p2,
        List<Vector3> vertices,
        Dictionary<(uint, uint), uint> cache
    )
    {
        uint smaller = Math.Min(p1, p2);
        uint larger = Math.Max(p1, p2);
        var key = (smaller, larger);

        if (cache.TryGetValue(key, out uint ret))
            return ret;

        Vector3 point1 = vertices[(int)p1];
        Vector3 point2 = vertices[(int)p2];
        Vector3 middle = Vector3.Normalize((point1 + point2) * 0.5f);

        ret = (uint)vertices.Count;
        vertices.Add(middle);
        cache.Add(key, ret);
        return ret;
    }
}

