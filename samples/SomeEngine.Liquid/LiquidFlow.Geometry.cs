using System.Numerics;

namespace SomeEngine.Liquid;

public sealed partial class LiquidFlow
{
    public float Distance(Vector3 position, out Vector3 normal) => Distance(position, out normal, out _);

    private bool ContactDistance(Vector3 position, LiquidContactSurface surface, out float distance, out Vector3 normal)
    {
        if (surface is LiquidContactSurface.CurvedTop or LiquidContactSurface.CurvedBottom)
        {
            float height = Surface.Height(position.X, position.Z, out normal);
            distance = (position.Y - height) * normal.Y;
            if (surface == LiquidContactSurface.CurvedBottom) { distance = -distance - Surface.Ripple.W; normal = -normal; }
            return Math.Abs(position.X) < Surface.Ripple.Y && Math.Abs(position.Z) < Surface.Ripple.Z;
        }
        distance = Distance(position, out normal, out LiquidContactSurface nearest);
        return surface == nearest;
    }

    private float Distance(Vector3 position, out Vector3 normal, out LiquidContactSurface surface)
    {
        float distance = position.Y - TrayFloor;
        normal = Vector3.UnitY; surface = LiquidContactSurface.TrayFloor;
        float wall = TrayHalfWidth * Extent - Math.Abs(position.X);
        if (wall < distance) { distance = wall; normal = new(-MathF.Sign(position.X), 0, 0); surface = LiquidContactSurface.TrayXWall; }
        wall = TrayHalfDepth * Extent - Math.Abs(position.Z);
        if (wall < distance) { distance = wall; normal = new(0, 0, -MathF.Sign(position.Z)); surface = LiquidContactSurface.TrayZWall; }
        if (Scene == "curved")
        {
            float separation = Surface.Distance(position, out Vector3 direction, out LiquidContactSurface face);
            if (separation < distance) { distance = separation; normal = direction; surface = face; }
        }
        else if (Scene == "pour")
        {
            for (int obstacle = 0; obstacle < Obstacles.Length; obstacle++)
            {
                float separation = Obstacles[obstacle].Distance(position, out Vector3 direction);
                if (separation < distance)
                { distance = separation; normal = direction; surface = obstacle == 0 ? RampFace(direction) : LiquidContactSurface.Sphere; }
            }
        }
        return distance;
    }

    private void ContainInTray(ref LiquidPoint point)
    {
        Vector3 minimum = new(-TrayHalfWidth * Extent + ContactRadius, TrayFloor + ContactRadius, -TrayHalfDepth * Extent + ContactRadius);
        Vector3 maximum = new(TrayHalfWidth * Extent - ContactRadius, float.PositiveInfinity, TrayHalfDepth * Extent - ContactRadius);
        Vector3 contained = Vector3.Clamp(point.Position, minimum, maximum);
        if (contained.X != point.Position.X) point.Velocity.X = contained.X > point.Position.X ? Math.Max(0, point.Velocity.X) : Math.Min(0, point.Velocity.X);
        if (contained.Y != point.Position.Y) point.Velocity.Y = Math.Max(0, point.Velocity.Y);
        if (contained.Z != point.Position.Z) point.Velocity.Z = contained.Z > point.Position.Z ? Math.Max(0, point.Velocity.Z) : Math.Min(0, point.Velocity.Z);
        point.Position = contained;
    }

    private LiquidContactSurface RampFace(Vector3 normal)
    {
        Vector4 rotation = Obstacles[0].Rotation;
        Vector3 local = Vector3.Transform(normal, Quaternion.Conjugate(new(rotation.X, rotation.Y, rotation.Z, rotation.W)));
        Vector3 size = Vector3.Abs(local);
        if (size.Y >= size.X && size.Y >= size.Z) return local.Y > 0 ? LiquidContactSurface.RampTop : LiquidContactSurface.RampBottom;
        if (size.X >= size.Z) return local.X > 0 ? LiquidContactSurface.RampPositiveX : LiquidContactSurface.RampNegativeX;
        return local.Z > 0 ? LiquidContactSurface.RampPositiveZ : LiquidContactSurface.RampNegativeZ;
    }

    private void UpdateObstacles()
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.34f);
        Obstacles[0] = new(new(-.55f, 1.28f, 0, 0), new(1.15f, .06f, .42f, 1), new(rotation.X, rotation.Y, rotation.Z, rotation.W));
        Obstacles[1] = new(new(.80f, .40f, Stirring ? MathF.Sin((float)Seconds * 1.8f) * .3f : .10f, 0), new(.34f, 0, 0, 0), new(0, 0, 0, 1));
    }
}
