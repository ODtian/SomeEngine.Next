namespace SomeEngine.Physics.Research;

// Independent double-precision SAT over the exported final box poses.
// The measurement uses geometry alone and runs outside PhysicsDemo.Step.
internal sealed unsafe class BoxPenetration(int count, int dimension, InputCollisionPair[] exclusions)
{
    private readonly Box[] boxes = new Box[count];
    private readonly int[] order = Enumerable.Range(0, count).ToArray();
    private readonly bool[,] excluded = Exclusions(count, exclusions);
    internal int First { get; private set; }
    internal int Second { get; private set; }
    internal int Contacts { get; private set; }

    internal double Read(BodyFrame[] frames)
    {
        for (int i = 0; i < frames.Length; ++i)
        {
            BodyFrame frame = frames[i];
            double x = frame.Rotation[0], y = frame.Rotation[1], z = frame.Rotation[2], w = frame.Rotation[3];
            double inverse = 1 / (x * x + y * y + z * z + w * w);
            var a = new Vector(1 - 2 * (y * y + z * z) * inverse, 2 * (x * y + w * z) * inverse, 2 * (x * z - w * y) * inverse);
            var b = new Vector(2 * (x * y - w * z) * inverse, 1 - 2 * (x * x + z * z) * inverse, 2 * (y * z + w * x) * inverse);
            var c = new Vector(2 * (x * z + w * y) * inverse, 2 * (y * z - w * x) * inverse, 1 - 2 * (x * x + y * y) * inverse);
            var half = new Vector(frame.Size[0] * .5, frame.Size[1] * .5, dimension == 2 ? 0 : frame.Size[2] * .5);
            var center = new Vector(frame.Position[0], frame.Position[1], dimension == 2 ? 0 : frame.Position[2]);
            Vector extent = a.Abs() * half.X + b.Abs() * half.Y + c.Abs() * half.Z;
            boxes[i] = new(center, half, a, b, c, center - extent, center + extent, frame.Mass > 0);
        }
        Array.Sort(order, (a, b) => boxes[a].Minimum.X.CompareTo(boxes[b].Minimum.X));
        double deepest = 0;
        First = Second = -1; Contacts = 0;
        for (int i = 0; i < order.Length; ++i)
        {
            int first = order[i];
            ref readonly Box a = ref boxes[first];
            for (int j = i + 1; j < order.Length && boxes[order[j]].Minimum.X <= a.Maximum.X; ++j)
            {
                int second = order[j];
                ref readonly Box b = ref boxes[second];
                if ((!a.Dynamic && !b.Dynamic) || excluded[first, second]
                    || a.Minimum.Y > b.Maximum.Y || b.Minimum.Y > a.Maximum.Y
                    || a.Minimum.Z > b.Maximum.Z || b.Minimum.Z > a.Maximum.Z) continue;
                double depth = Depth(a, b);
                if (depth <= 0) continue;
                ++Contacts;
                if (depth > deepest) { deepest = depth; First = first; Second = second; }
            }
        }
        return deepest;
    }

    private double Depth(in Box a, in Box b)
    {
        Vector distance = b.Center - a.Center;
        double depth = double.PositiveInfinity;
        int axes = dimension;
        for (int axis = 0; axis < axes; ++axis)
        {
            if (!Axis(a.Axis(axis), a, b, distance, ref depth)) return 0;
            if (!Axis(b.Axis(axis), a, b, distance, ref depth)) return 0;
        }
        if (dimension == 3)
            for (int first = 0; first < 3; ++first)
                for (int second = 0; second < 3; ++second)
                {
                    Vector axis = a.Axis(first).Cross(b.Axis(second));
                    double square = axis.Dot(axis);
                    if (square > 1e-20 && !Axis(axis * (1 / Math.Sqrt(square)), a, b, distance, ref depth)) return 0;
                }
        return depth;
    }

    private static bool Axis(Vector axis, in Box a, in Box b, Vector distance, ref double depth)
    {
        double separation = a.Radius(axis) + b.Radius(axis) - Math.Abs(distance.Dot(axis));
        depth = Math.Min(depth, separation);
        return separation > 0;
    }

    private static bool[,] Exclusions(int count, InputCollisionPair[] pairs)
    {
        var result = new bool[count, count];
        foreach (var pair in pairs) result[pair.BodyA, pair.BodyB] = result[pair.BodyB, pair.BodyA] = true;
        return result;
    }

    private readonly record struct Box(Vector Center, Vector Half, Vector X, Vector Y, Vector Z,
        Vector Minimum, Vector Maximum, bool Dynamic)
    {
        internal Vector Axis(int index) => index switch { 0 => X, 1 => Y, _ => Z };
        internal double Radius(Vector axis) => Half.X * Math.Abs(X.Dot(axis)) + Half.Y * Math.Abs(Y.Dot(axis)) + Half.Z * Math.Abs(Z.Dot(axis));
    }

    private readonly record struct Vector(double X, double Y, double Z)
    {
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector operator *(Vector a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        internal double Dot(Vector b) => X * b.X + Y * b.Y + Z * b.Z;
        internal Vector Cross(Vector b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        internal Vector Abs() => new(Math.Abs(X), Math.Abs(Y), Math.Abs(Z));
    }
}
