using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Clothing.Demo;

public readonly record struct SurfaceContact(Vector3 Position, Vector3 Normal, Vector3 Velocity, int Triangle = -1)
{
    public Vector3 Project(Vector3 point, float clearance = .008f)
    {
        float separation = Vector3.Dot(point - Position, Normal);
        if (separation < clearance) point += Normal * (clearance - separation);
        return point;
    }

    public float Separation(Vector3 point) => Normal == Vector3.Zero
        ? float.PositiveInfinity : Vector3.Dot(point - Position, Normal);

    public Vector3 ProjectVelocity(Vector3 point, Vector3 velocity)
    {
        if (Separation(point) > .009f) return velocity;
        float incoming = Vector3.Dot(velocity - Velocity, Normal);
        return incoming < 0 ? velocity - incoming * Normal : velocity;
    }
}

public readonly record struct ClothingContact(SurfaceContact Body)
{
    public const float GroundHeight = .001f;
    public Vector3 Project(Vector3 point)
    {
        point = Body.Project(point);
        point.Y = MathF.Max(GroundHeight, point.Y);
        return point;
    }

    public float MinimumSeparation(Vector3 point) => Body.Separation(point);

    public Vector3 ProjectVelocity(Vector3 point, Vector3 velocity)
    {
        velocity = Body.ProjectVelocity(point, velocity);
        if (point.Y <= GroundHeight + .0001f && velocity.Y < 0) velocity.Y = 0;
        return velocity;
    }
}

public sealed class BodyMesh
{
    public Vector3[] Positions { get; }
    public Vector3[] Normals { get; }
    public int[] Triangles => surface.Triangles;
    public SurfaceTriangles Surface { get; }
    public FootHull[] Feet { get; }
    private readonly CharacterSurface surface;
    private readonly Vector3[] previousPositions;
    private readonly Vector3[] contactPositions;
    private readonly Vector3[] previousContactPositions;
    private float seconds;
    private readonly Quaternion[] footRotations = new Quaternion[2];

    public BodyMesh(CharacterAsset character)
    {
        surface = character.Body;
        Positions = surface.Positions.ToArray();
        Normals = surface.Normals.ToArray();
        previousPositions = Positions.ToArray();
        contactPositions = Positions.ToArray();
        previousContactPositions = Positions.ToArray();
        Surface = new(contactPositions, surface.Triangles);
        Feet = [FootHull.Create(character, 0), FootHull.Create(character, 1)];
    }

    public void Update(BodyMotion motion, bool dualQuaternion, float seconds)
    {
        Positions.CopyTo(previousPositions, 0);
        this.seconds = seconds;
        if (dualQuaternion)
            JobSystem.ExecuteParallel(new SkinDualQuaternionVertices(surface, motion, Positions, Normals), Positions.Length, 256);
        else
            JobSystem.ExecuteParallel(new SkinMatrixVertices(surface, motion, Positions, Normals), Positions.Length, 256);
        footRotations[0] = motion.Bones[Feet[0].Bone].Rotation;
        footRotations[1] = motion.Bones[Feet[1].Bone].Rotation;
    }

    public void UpdateContactSurface(float blend)
    {
        contactPositions.CopyTo(previousContactPositions, 0);
        JobSystem.ExecuteParallel(new InterpolateContactVertices(previousPositions, Positions, contactPositions, blend), Positions.Length, 256);
        Surface.Update();
        for (int foot = 0; foot < Feet.Length; foot++) Feet[foot].Update(contactPositions, footRotations[foot]);
    }

    private readonly struct SkinDualQuaternionVertices(CharacterSurface source, BodyMotion motion,
        Vector3[] positions, Vector3[] normals) : IJobParallelFor
    {
        public void Execute(int index)
        {
            DualQuaternion transform = motion.Blend(source.BoneWeights[index]);
            positions[index] = transform.Transform(source.Positions[index]);
            normals[index] = Vector3.Transform(source.Normals[index], transform.Rotation);
        }
    }

    private readonly struct SkinMatrixVertices(CharacterSurface source, BodyMotion motion,
        Vector3[] positions, Vector3[] normals) : IJobParallelFor
    {
        public void Execute(int index)
        {
            Matrix4x4 transform = motion.BlendMatrix(source.BoneWeights[index]);
            positions[index] = Vector3.Transform(source.Positions[index], transform);
            normals[index] = Vector3.Normalize(Vector3.TransformNormal(source.Normals[index], transform));
        }
    }

    private readonly struct InterpolateContactVertices(Vector3[] previous, Vector3[] positions,
        Vector3[] contacts, float blend) : IJobParallelFor
    {
        public void Execute(int index) => contacts[index] = Vector3.Lerp(previous[index], positions[index], blend);
    }

    public void ResetMotion()
    {
        Positions.CopyTo(previousPositions, 0);
        Positions.CopyTo(contactPositions, 0);
        Positions.CopyTo(previousContactPositions, 0);
    }

    public Vector3 GetPreviousContactPoint(int triangle, Vector3 coordinates)
        => previousContactPositions[Triangles[triangle * 3]] * coordinates.X
            + previousContactPositions[Triangles[triangle * 3 + 1]] * coordinates.Y
            + previousContactPositions[Triangles[triangle * 3 + 2]] * coordinates.Z;
    public ClothingContact FindContact(Vector3 position, int triangle = -1)
    {
        SurfacePoint closest = Surface.Nearest(position, triangle, .05f);
        if (closest.Triangle < 0) return default;
        return FindContact(closest.Triangle, closest.Coordinates);
    }

    public ClothingContact FindContact(Vector3 previousPosition, Vector3 position, int triangle = -1)
    {
        SurfacePoint closest = Surface.Nearest(position, triangle, .05f);
        if (closest.Triangle < 0) return default;
        ClothingContact contact = FindContact(closest.Triangle, closest.Coordinates);
        Vector3 movement = contact.Body.Position - GetPreviousContactPoint(closest.Triangle, closest.Coordinates);
        SurfacePoint crossing = Surface.IntersectSegment(previousPosition + movement, position);
        return crossing.Triangle >= 0 ? FindContact(crossing.Triangle, crossing.Coordinates) : contact;
    }

    public ClothingContact FindContact(int triangle, Vector3 coordinates)
    {
        Vector3 position = Vector3.Zero;
        Vector3 normal = Vector3.Zero, velocity = Vector3.Zero;
        for (int corner = 0; corner < 3; corner++)
        {
            int vertex = Triangles[triangle * 3 + corner];
            position += contactPositions[vertex] * coordinates[corner];
            normal += Normals[vertex] * coordinates[corner];
            velocity += (Positions[vertex] - previousPositions[vertex]) * (coordinates[corner] / seconds);
        }
        return new(new(position, Vector3.Normalize(normal), velocity, triangle));
    }
}
