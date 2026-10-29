using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public static class CollisionGeometry<TFirst, TSecond> where TFirst : struct where TSecond : struct
{
    public static GeometryPairPrediction<TFirst, TSecond, TFirstMotion, TSecondMotion, TKernel, TOutput>
        Predict<TFirstMotion, TSecondMotion, TKernel, TOutput>(TFirstMotion firstMotion, TSecondMotion secondMotion,
            TKernel kernel, TOutput output, float contactDistance)
        where TFirstMotion : struct, ICollisionGeometryMotion<TFirst>
        where TSecondMotion : struct, ICollisionGeometryMotion<TSecond>
        where TKernel : struct, ICollisionNarrowPhase<TFirst, TSecond>
        where TOutput : struct, ICollisionContactOutput => new(firstMotion, secondMotion, kernel, output, contactDistance);

    public static ReverseCollision<TFirst, TSecond, TKernel> Reverse<TKernel>(TKernel kernel)
        where TKernel : struct, ICollisionNarrowPhase<TFirst, TSecond> => new(kernel);

    public static GeometryPairDetection<TFirst, TSecond, ComponentGeometry<TFirst>, ComponentGeometry<TSecond>,
        TKernel, TOutput> Detect<TKernel, TOutput>(TKernel kernel, TOutput output, float contactDistance)
        where TKernel : struct, ICollisionNarrowPhase<TFirst, TSecond>
        where TOutput : struct, ICollisionContactOutput => new(default, default, kernel, output, contactDistance);

    public static GeometryPairDetection<TFirst, TSecond, TFirstReader, TSecondReader, TKernel, TOutput>
        Detect<TFirstReader, TSecondReader, TKernel, TOutput>(TFirstReader firstReader, TSecondReader secondReader,
            TKernel kernel, TOutput output, float contactDistance)
        where TFirstReader : struct, ICollisionGeometryReader<TFirst>
        where TSecondReader : struct, ICollisionGeometryReader<TSecond>
        where TKernel : struct, ICollisionNarrowPhase<TFirst, TSecond>
        where TOutput : struct, ICollisionContactOutput => new(firstReader, secondReader, kernel, output, contactDistance);
}

/// <summary>Runs one concrete geometry pair kernel and writes contacts with their actual endpoint weights.</summary>
public struct GeometryPairDetection<TFirst, TSecond, TFirstReader, TSecondReader, TKernel, TOutput>(
    TFirstReader firstReader, TSecondReader secondReader, TKernel kernel, TOutput output, float contactDistance)
    : ICollisionPairQueryDetection
    where TFirst : struct
    where TSecond : struct
    where TFirstReader : struct, ICollisionGeometryReader<TFirst>
    where TSecondReader : struct, ICollisionGeometryReader<TSecond>
    where TKernel : struct, ICollisionNarrowPhase<TFirst, TSecond>
    where TOutput : struct, ICollisionContactOutput
{
    public QueryDefinition AddReads(QueryDefinition definition) => secondReader.AddReads(firstReader.AddReads(definition));

    public void Execute(in CollisionGeometryPair pair, scoped QueryCursor access,
        scoped QueryRow first, scoped QueryRow second)
    {
        TFirst firstGeometry = firstReader.ReadGeometry(first, access, out BodyPose firstPose);
        TSecond secondGeometry = secondReader.ReadGeometry(second, access, out BodyPose secondPose);
        Span<CollisionDistance> points = stackalloc CollisionDistance[kernel.ContactCapacity];
        int count = kernel.GetManifold(in firstGeometry, in firstPose, in secondGeometry, in secondPose,
            contactDistance, points);
        Span<ContactBody> firstBodies = stackalloc ContactBody[firstReader.GetBodyCount(first, access)];
        Span<ContactBody> secondBodies = stackalloc ContactBody[secondReader.GetBodyCount(second, access)];
        for (int index = 0; index < count; index++)
        {
            ref readonly CollisionDistance point = ref points[index];
            firstReader.GetBodies(first, access, point.FirstPoint, firstBodies);
            secondReader.GetBodies(second, access, point.SecondPoint, secondBodies);
            var contact = new CollisionContact
            {
                First = pair.First,
                Second = pair.Second,
                FirstFeature = point.FirstFeature,
                SecondFeature = point.SecondFeature,
                PointIndex = index,
                FirstLocalPoint = GeometrySpace.ToLocal(in firstPose, point.FirstPoint),
                SecondLocalPoint = GeometrySpace.ToLocal(in secondPose, point.SecondPoint),
                WorldPoint = (point.FirstPoint + point.SecondPoint) * 0.5f,
                Normal = point.Normal,
                Separation = point.Separation,
                FirstBodyCount = firstBodies.Length,
            };
            output.Add(in contact, firstBodies, secondBodies);
        }
    }
}
