using System.Numerics;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>Samples a starting separation plane and tests the common predicted endpoint displacement against it.</summary>
public struct GeometryPairPrediction<TFirst, TSecond, TFirstMotion, TSecondMotion, TKernel, TOutput>(
    TFirstMotion firstMotion, TSecondMotion secondMotion, TKernel kernel, TOutput output, float contactDistance)
    : IPhysicsCollisionPairDetection
    where TFirst : struct where TSecond : struct
    where TFirstMotion : struct, ICollisionGeometryMotion<TFirst>
    where TSecondMotion : struct, ICollisionGeometryMotion<TSecond>
    where TKernel : struct, ICollisionNarrowPhase<TFirst, TSecond>
    where TOutput : struct, ICollisionContactOutput
{
    public QueryDefinition AddReads(QueryDefinition definition) => secondMotion.AddReads(firstMotion.AddReads(definition));

    public void Execute(in CollisionGeometryPair pair, scoped QueryCursor access, scoped QueryRow first, scoped QueryRow second,
        in PhysicsStepContext context)
    {
        TFirst firstGeometry = firstMotion.ReadStartGeometry(first, access, out BodyPose firstPose);
        TSecond secondGeometry = secondMotion.ReadStartGeometry(second, access, out BodyPose secondPose);
        CollisionDistance nearest = kernel.Distance(in firstGeometry, in firstPose, in secondGeometry, in secondPose);
        Span<CollisionDistance> points = stackalloc CollisionDistance[kernel.ContactCapacity];
        CollisionBounds firstBounds = firstMotion.ReadMotionBounds(first, access, in context, 0);
        CollisionBounds secondBounds = secondMotion.ReadMotionBounds(second, access, in context, 0);
        float firstTravel = MathF.Max(0, GetMaximumProjection(in firstBounds, nearest.Normal) - Vector3.Dot(nearest.Normal, nearest.FirstPoint));
        float secondTravel = MathF.Max(0, GetMaximumProjection(in secondBounds, -nearest.Normal) + Vector3.Dot(nearest.Normal, nearest.SecondPoint));
        float manifoldDistance = MathF.Max(contactDistance, nearest.Separation) + firstTravel + secondTravel;
        int count = kernel.GetManifold(in firstGeometry, in firstPose, in secondGeometry, in secondPose, manifoldDistance, points);
        if (count == 0 && nearest.Separation > contactDistance) { points[0] = nearest; count = 1; }
        Span<ContactBody> firstBodies = stackalloc ContactBody[firstMotion.GetBodyCount(first, access)];
        Span<ContactBody> secondBodies = stackalloc ContactBody[secondMotion.GetBodyCount(second, access)];
        for (int index = 0; index < count; index++)
        {
            ref readonly CollisionDistance point = ref points[index];
            firstMotion.GetStartBodies(first, access, point.FirstPoint, firstBodies);
            secondMotion.GetStartBodies(second, access, point.SecondPoint, secondBodies);
            Vector3 startDelta = BodyStepGeometry.Delta(firstBodies, secondBodies, access, false);
            Vector3 predictedDelta = BodyStepGeometry.Delta(firstBodies, secondBodies, access, true);
            float gap = point.Separation + Vector3.Dot(point.Normal, predictedDelta - startDelta);
            if (point.Separation > contactDistance && gap > contactDistance) continue;
            var contact = new CollisionContact
            {
                First = pair.First, Second = pair.Second, FirstFeature = point.FirstFeature, SecondFeature = point.SecondFeature,
                PointIndex = index, FirstLocalPoint = GeometrySpace.ToLocal(in firstPose, point.FirstPoint),
                SecondLocalPoint = GeometrySpace.ToLocal(in secondPose, point.SecondPoint),
                WorldPoint = (point.FirstPoint + point.SecondPoint) * .5f, Normal = point.Normal, Separation = point.Separation,
                FirstBodyCount = firstBodies.Length, Prediction = new ContactPrediction(true, context.StartTime),
            };
            output.Add(in contact, firstBodies, secondBodies);
        }
    }

    private static float GetMaximumProjection(in CollisionBounds bounds, Vector3 direction) => Vector3.Dot(new Vector3(
        direction.X >= 0 ? bounds.Max.X : bounds.Min.X,
        direction.Y >= 0 ? bounds.Max.Y : bounds.Min.Y,
        direction.Z >= 0 ? bounds.Max.Z : bounds.Min.Z), direction);
}
