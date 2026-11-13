using System.Numerics;
using SomeEngine.ECS;

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
    public bool SupportsParallel => output.SupportsParallel;
    public void BeginParallel(int partitions) => output.BeginParallel(partitions);
    public void SelectPartition(int partition) => output.SelectPartition(partition);
    public QueryDescription AddReads(QueryDescription definition) => secondMotion.AddReads(firstMotion.AddReads(definition));
    public void AddInputAccesses(World world, List<SomeEngine.Job.JobResourceAccess> accesses)
    { firstMotion.AddInputAccesses(world, accesses); secondMotion.AddInputAccesses(world, accesses); }
    public void AddAccesses(World world, Query query, List<SomeEngine.Job.JobResourceAccess> accesses)
    { firstMotion.AddAccesses(world, accesses); secondMotion.AddAccesses(world, accesses); }


    public void Execute(in CollisionGeometryPair pair, World world,
        in PhysicsStepContext context)
    {
        if (context.UseCurrentContacts)
        {
            var current = new GeometryPairDetection<TFirst, TSecond, TFirstMotion, TSecondMotion, TKernel, TOutput>(
                firstMotion, secondMotion, kernel, output, contactDistance);
            current.Execute(in pair, world);
            return;
        }
        TFirst firstGeometry = firstMotion.ReadStartGeometry(world, pair.First, out BodyPose firstPose);
        TSecond secondGeometry = secondMotion.ReadStartGeometry(world, pair.Second, out BodyPose secondPose);
        CollisionDistance nearest = kernel.Distance(in firstGeometry, in firstPose, in secondGeometry, in secondPose);
        Span<CollisionDistance> points = stackalloc CollisionDistance[kernel.ContactCapacity];
        CollisionBounds firstBounds = firstMotion.ReadMotionBounds(world, pair.First, in context, 0);
        CollisionBounds secondBounds = secondMotion.ReadMotionBounds(world, pair.Second, in context, 0);
        float firstTravel = MathF.Max(0, GetMaximumProjection(in firstBounds, nearest.Normal) - Vector3.Dot(nearest.Normal, nearest.FirstPoint));
        float secondTravel = MathF.Max(0, GetMaximumProjection(in secondBounds, -nearest.Normal) + Vector3.Dot(nearest.Normal, nearest.SecondPoint));
        float manifoldDistance = MathF.Max(contactDistance, nearest.Separation) + firstTravel + secondTravel;
        int count = kernel.GetManifold(in firstGeometry, in firstPose, in secondGeometry, in secondPose, manifoldDistance, points);
        if (count == 0 && nearest.Separation > contactDistance) { points[0] = nearest; count = 1; }
        Span<ContactBody> firstBodies = stackalloc ContactBody[firstMotion.GetBodyCount(world, pair.First)];
        Span<ContactBody> secondBodies = stackalloc ContactBody[secondMotion.GetBodyCount(world, pair.Second)];
        for (int index = 0; index < count; index++)
        {
            ref readonly CollisionDistance point = ref points[index];
            firstMotion.GetStartBodies(world, pair.First, point.FirstPoint, firstBodies);
            secondMotion.GetStartBodies(world, pair.Second, point.SecondPoint, secondBodies);
            Vector3 startDelta = BodyStepGeometry.Delta(firstBodies, secondBodies, world, false, normal: true);
            Vector3 predictedDelta = BodyStepGeometry.Delta(firstBodies, secondBodies, world, true, normal: true);
            float gap = point.Separation + Vector3.Dot(point.Normal, predictedDelta - startDelta);
            if (point.Separation > contactDistance && gap > contactDistance) continue;
            var contact = new CollisionContact
            {
                First = pair.First,
                FirstShape = (uint)pair.FirstGeometry,
                SecondShape = (uint)pair.SecondGeometry,
                Second = pair.Second,
                FirstFeature = point.FirstFeature,
                SecondFeature = point.SecondFeature,
                PointIndex = index,
                FirstLocalPoint = GeometrySpace.ToLocal(in firstPose, point.FirstPoint),
                SecondLocalPoint = GeometrySpace.ToLocal(in secondPose, point.SecondPoint),
                WorldPoint = (point.FirstPoint + point.SecondPoint) * .5f,
                Normal = point.Normal,
                Separation = point.Separation,
                FirstBodyCount = firstBodies.Length,
                Prediction = new ContactPrediction(true, context.StartTime),
            };
            output.Add(in contact, firstBodies, secondBodies);
        }
    }

    private static float GetMaximumProjection(in CollisionBounds bounds, Vector3 direction) => Vector3.Dot(new Vector3(
        direction.X >= 0 ? bounds.Max.X : bounds.Min.X,
        direction.Y >= 0 ? bounds.Max.Y : bounds.Min.Y,
        direction.Z >= 0 ? bounds.Max.Z : bounds.Min.Z), direction);
}
