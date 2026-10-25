using System.Numerics;

namespace SomeEngine.Physics;

/// <summary>An outward unit normal and a polygon range in the hull's face-vertex indices.</summary>
public readonly record struct HullFace(Vector3 Normal, int FirstIndex, int IndexCount);

/// <summary>Project-supplied, ready-to-read convex geometry shared by its entity components.</summary>
public sealed class ConvexHullData
{
    public ReadOnlyMemory<Vector3> Vertices { get; }
    public ReadOnlyMemory<HullFace> Faces { get; }
    public ReadOnlyMemory<int> FaceVertices { get; }
    public int MaximumFaceVertices { get; }

    /// <summary>The project keeps these arrays immutable and supplies outward-wound convex faces.</summary>
    public ConvexHullData(ReadOnlyMemory<Vector3> vertices, ReadOnlyMemory<HullFace> faces,
        ReadOnlyMemory<int> faceVertices)
    {
        Vertices = vertices;
        Faces = faces;
        FaceVertices = faceVertices;
        foreach (HullFace face in faces.Span)
            MaximumFaceVertices = Math.Max(MaximumFaceVertices, face.IndexCount);
    }
}
