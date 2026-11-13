using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly partial struct XpbdLengthProjection
{
    /// <summary>Projects complete vectors of independent constraints; the caller projects the remaining entities.</summary>
    int IXpbdConstraint<XpbdLength>.ProjectRange(ReadOnlySpan<PhysicsConstraintIdentity> rows, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        int width = Vector<float>.Count;
        int count = rows.Length - rows.Length % width;
        Span<Entity> firstBodies = stackalloc Entity[width];
        Span<Entity> secondBodies = stackalloc Entity[width];
        Span<float> values = stackalloc float[15 * width];
        Span<float> differenceX = values.Slice(0 * width, width);
        Span<float> differenceY = values.Slice(1 * width, width);
        Span<float> differenceZ = values.Slice(2 * width, width);
        Span<float> firstWeights = values.Slice(3 * width, width);
        Span<float> secondWeights = values.Slice(4 * width, width);
        Span<float> restLengths = values.Slice(5 * width, width);
        Span<float> compliances = values.Slice(6 * width, width);
        Span<float> multipliers = values.Slice(7 * width, width);
        Span<float> multiplierChanges = values.Slice(8 * width, width);
        Span<float> firstChangesX = values.Slice(9 * width, width);
        Span<float> firstChangesY = values.Slice(10 * width, width);
        Span<float> firstChangesZ = values.Slice(11 * width, width);
        Span<float> secondChangesX = values.Slice(12 * width, width);
        Span<float> secondChangesY = values.Slice(13 * width, width);
        Span<float> secondChangesZ = values.Slice(14 * width, width);
        float duration = (float)context.Duration;
        var squaredDuration = new Vector<float>(duration * duration);

        for (int start = 0; start < count; start += width)
        {
            for (int lane = 0; lane < width; lane++)
            {
                Entity entity = rows[start + lane].Entity;
                ref readonly XpbdLength constraint = ref _fields.ReadConstraint(entity);
                ref readonly var endpoints = ref _fields.ReadEndpoints(entity);
                firstBodies[lane] = endpoints.Source;
                secondBodies[lane] = endpoints.Target;
                Vector3 difference = bodies.ReadPose(endpoints.Target).Position - bodies.ReadPose(endpoints.Source).Position;
                ref readonly BodyMass firstMass = ref bodies.ReadMass(endpoints.Source);
                ref readonly BodyMass secondMass = ref bodies.ReadMass(endpoints.Target);
                differenceX[lane] = difference.X;
                differenceY[lane] = difference.Y;
                differenceZ[lane] = difference.Z;
                firstWeights[lane] = firstMass.Motion == BodyMotion.Dynamic ? firstMass.InverseMass : 0;
                secondWeights[lane] = secondMass.Motion == BodyMotion.Dynamic ? secondMass.InverseMass : 0;
                restLengths[lane] = constraint.RestLength;
                compliances[lane] = constraint.Compliance;
                multipliers[lane] = constraint.Multiplier;
            }

            var x = new Vector<float>(differenceX);
            var y = new Vector<float>(differenceY);
            var z = new Vector<float>(differenceZ);
            var firstWeight = new Vector<float>(firstWeights);
            var secondWeight = new Vector<float>(secondWeights);
            Vector<float> distance = Vector.SquareRoot(x * x + y * y + z * z);
            Vector<float> gradientX = x / distance;
            Vector<float> gradientY = y / distance;
            Vector<float> gradientZ = z / distance;
            Vector<float> alpha = new Vector<float>(compliances) / squaredDuration;
            Vector<float> change = (-(distance - new Vector<float>(restLengths)) - alpha * new Vector<float>(multipliers)) /
                (firstWeight + secondWeight + alpha);
            change.CopyTo(multiplierChanges);
            (firstWeight * gradientX * change).CopyTo(firstChangesX);
            (firstWeight * gradientY * change).CopyTo(firstChangesY);
            (firstWeight * gradientZ * change).CopyTo(firstChangesZ);
            (secondWeight * gradientX * change).CopyTo(secondChangesX);
            (secondWeight * gradientY * change).CopyTo(secondChangesY);
            (secondWeight * gradientZ * change).CopyTo(secondChangesZ);

            for (int lane = 0; lane < width; lane++)
            {
                _fields.WriteState(rows[start + lane].Entity).Multiplier += multiplierChanges[lane];
                if (firstWeights[lane] > 0)
                    bodies.WritePose(firstBodies[lane]).Position -= new Vector3(firstChangesX[lane], firstChangesY[lane], firstChangesZ[lane]);
                if (secondWeights[lane] > 0)
                    bodies.WritePose(secondBodies[lane]).Position += new Vector3(secondChangesX[lane], secondChangesY[lane], secondChangesZ[lane]);
            }
        }
        return count;
    }
}
