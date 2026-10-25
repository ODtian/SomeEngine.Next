using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Physics.Research;

internal sealed unsafe class LongRangeConstraints
{
    private readonly DemoLibrary library;
    private readonly nint demo;
    private readonly BodyFrame[] bodies;
    private readonly InputDistanceRange[]? distanceRanges;
    private readonly PhysicsMaterialModel materialModel;
    private int jointVersion = -1;

    public LongRangeConstraints(DemoLibrary library, nint demo, InputDistanceRange[]? distanceRanges, PhysicsMaterialModel materialModel)
    {
        this.library = library;
        this.demo = demo;
        this.distanceRanges = distanceRanges;
        this.materialModel = materialModel;
        bodies = distanceRanges is null && materialModel == PhysicsMaterialModel.RigidConstraints ? new BodyFrame[library.BodyCount(demo)] : [];
        RefreshJoints();
    }

    private void RefreshJoints()
    {
        int version = library.ReadJointVersion(demo);
        if (jointVersion == version) return;
        jointVersion = version;
        DistanceRange[] ranges;
        if (distanceRanges is null && materialModel == PhysicsMaterialModel.CompliantMaterials) ranges = [];
        else if (distanceRanges is null)
        {
            fixed (BodyFrame* output = bodies) library.ReadFrame(demo, output);
            var joints = new JointData[library.ReadJoints(demo, null)];
            fixed (JointData* output = joints) library.ReadJoints(demo, output);
            ranges = new JointDistances(bodies, joints).Build();
        }
        else
        {
            ranges = distanceRanges.Select(range => BodyGeometry.CreateRange(range.BodyA, range.BodyB,
                new((float)range.PointA[0], (float)range.PointA[1], (float)range.PointA[2]),
                new((float)range.PointB[0], (float)range.PointB[1], (float)range.PointB[2]),
                range.Minimum, range.Maximum)).ToArray();
        }
        fixed (DistanceRange* rangeData = ranges)
            library.SetJointRanges(demo, rangeData, ranges.Length);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void BuildSupports(nint context) => ((LongRangeConstraints)GCHandle.FromIntPtr(context).Target!).BuildSupports();

    private void BuildSupports()
    {
        RefreshJoints();
        library.BuildSupports(demo);
    }
}

internal static unsafe class BodyGeometry
{
    public static Vector3 ReadPosition(BodyFrame body) => Read(body.Position);
    public static Vector3 Read(double* value) => new((float)value[0], (float)value[1], (float)value[2]);
    public static void Write(double* target, Vector3 value)
    {
        target[0] = value.X; target[1] = value.Y; target[2] = value.Z;
    }
    public static DistanceRange CreateRange(int bodyA, int bodyB, Vector3 anchorA, Vector3 anchorB, double minimum, double maximum)
    {
        DistanceRange range = new() { BodyA = bodyA, BodyB = bodyB, Minimum = minimum, Maximum = maximum };
        Write(range.PointA, anchorA); Write(range.PointB, anchorB);
        return range;
    }
}
