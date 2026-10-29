using System.Text.Json.Nodes;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

namespace DefaultGame;

/// <summary>The original acceptance's independent double-precision SAT over completed box geometry.</summary>
internal sealed class SceneBoxAccuracy
{
    private const double ThresholdMeters = .005;
    private readonly Dictionary<Entity, int> _bodyIndices;
    private readonly HashSet<(int First, int Second)> _excluded = [];
    private readonly Box[] _boxes;
    private readonly int[] _order;
    private readonly Comparison<int> _compareMinimumX;
    private readonly int _dimension, _dynamicBodies, _stepCount;
    private readonly long _allowedPairs;
    private int _completedSteps, _first = -1, _second = -1;
    private long _deepestStep = -1;
    private double _currentPenetration, _maximumPenetration;

    internal SceneBoxAccuracy(World world, PhysicsSceneSimulation scene, int stepCount)
    {
        _dimension = scene.Description.Dimension; _stepCount = stepCount;
        int count = scene.Bodies.Length;
        _bodyIndices = new(count);
        _boxes = new Box[count];
        _order = new int[count];
        _compareMinimumX = (first, second) => _boxes[first].Minimum.X.CompareTo(_boxes[second].Minimum.X);
        for (int index = 0; index < count; index++)
        {
            Entity body = scene.Bodies.Span[index];
            _bodyIndices.Add(body, index);
            bool dynamic = world.Read<BodyMass>(body).Motion == BodyMotion.Dynamic;
            _boxes[index] = _boxes[index] with { Dynamic = dynamic };
            if (dynamic) _dynamicBodies++;
            _order[index] = index;
        }
        QueryHandle exclusions = world.Query(new QueryDefinitionBuilder().All<SceneCollisionExclusion>()
            .Read<UndirectedRelationEndpoints<SceneCollisionExclusion>>());
        try
        {
            world.ExecuteReadQuery(exclusions, rows =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    var ends = row.Read<UndirectedRelationEndpoints<SceneCollisionExclusion>>();
                    int first = _bodyIndices[ends.EndpointA], second = _bodyIndices[ends.EndpointB];
                    _excluded.Add((Math.Min(first, second), Math.Max(first, second)));
                }
            });
        }
        finally { world.ReleaseQuery(exclusions); }
        long staticBodies = count - _dynamicBodies;
        _allowedPairs = count * (count - 1L) / 2 - staticBodies * (staticBodies - 1) / 2
            - _excluded.Count(pair => _boxes[pair.First].Dynamic || _boxes[pair.Second].Dynamic);
    }

    internal bool Satisfied => _completedSteps == _stepCount && _maximumPenetration <= ThresholdMeters;

    internal void ReadBody(Entity entity, in BodyPose pose, in BodyMass mass, in BoxGeometry geometry)
    {
        double x = pose.Rotation.X, y = pose.Rotation.Y, z = pose.Rotation.Z, w = pose.Rotation.W;
        double inverse = 1 / (x * x + y * y + z * z + w * w);
        var a = new Vector(1 - 2 * (y * y + z * z) * inverse, 2 * (x * y + w * z) * inverse, 2 * (x * z - w * y) * inverse);
        var b = new Vector(2 * (x * y - w * z) * inverse, 1 - 2 * (x * x + z * z) * inverse, 2 * (y * z + w * x) * inverse);
        var c = new Vector(2 * (x * z + w * y) * inverse, 2 * (y * z - w * x) * inverse, 1 - 2 * (x * x + y * y) * inverse);
        var half = new Vector(geometry.HalfExtents.X, geometry.HalfExtents.Y, _dimension == 2 ? 0 : geometry.HalfExtents.Z);
        var center = new Vector(pose.Position.X, pose.Position.Y, _dimension == 2 ? 0 : pose.Position.Z);
        Vector extent = a.Abs() * half.X + b.Abs() * half.Y + c.Abs() * half.Z;
        _boxes[_bodyIndices[entity]] = new(center, half, a, b, c, center - extent, center + extent,
            mass.Motion == BodyMotion.Dynamic);
    }

    internal void FinishRead(long step)
    {
        Array.Sort(_order, _compareMinimumX);
        _currentPenetration = 0;
        for (int index = 0; index < _order.Length; index++)
        {
            int first = _order[index];
            ref readonly Box a = ref _boxes[first];
            for (int next = index + 1; next < _order.Length && _boxes[_order[next]].Minimum.X <= a.Maximum.X; next++)
            {
                int second = _order[next];
                ref readonly Box b = ref _boxes[second];
                if ((!a.Dynamic && !b.Dynamic) || _excluded.Contains((Math.Min(first, second), Math.Max(first, second)))
                    || a.Minimum.Y > b.Maximum.Y || b.Minimum.Y > a.Maximum.Y
                    || a.Minimum.Z > b.Maximum.Z || b.Minimum.Z > a.Maximum.Z) continue;
                double depth = Depth(in a, in b);
                _currentPenetration = Math.Max(_currentPenetration, depth);
                if (depth > _maximumPenetration)
                {
                    _maximumPenetration = depth;
                    _deepestStep = step; _first = first; _second = second;
                }
            }
        }
        _completedSteps++;
    }

    internal JsonObject Describe() => new()
    {
        ["Scope"] = "Independent double SAT over every original completed BoxGeometry and BodyPose; static-static and original collision exclusions skipped",
        ["Dimension"] = _dimension, ["Bodies"] = _boxes.Length, ["DynamicBodies"] = _dynamicBodies,
        ["ExcludedPairs"] = _excluded.Count, ["AllowedPairs"] = _allowedPairs,
        ["ThresholdMeters"] = ThresholdMeters, ["CompletedSteps"] = _completedSteps, ["OriginalSteps"] = _stepCount,
        ["CurrentPenetrationMeters"] = _currentPenetration, ["MaximumPenetrationMeters"] = _maximumPenetration,
        ["DeepestStep"] = _deepestStep, ["FirstBody"] = _first, ["SecondBody"] = _second,
    };

    private double Depth(in Box a, in Box b)
    {
        Vector distance = b.Center - a.Center;
        double depth = double.PositiveInfinity;
        for (int axis = 0; axis < _dimension; axis++)
        {
            if (!Overlaps(a.Axis(axis), in a, in b, distance, ref depth)) return 0;
            if (!Overlaps(b.Axis(axis), in a, in b, distance, ref depth)) return 0;
        }
        if (_dimension == 3)
            for (int first = 0; first < 3; first++)
                for (int second = 0; second < 3; second++)
                {
                    Vector axis = a.Axis(first).Cross(b.Axis(second));
                    double square = axis.Dot(axis);
                    if (square > 1e-20 && !Overlaps(axis * (1 / Math.Sqrt(square)), in a, in b, distance, ref depth)) return 0;
                }
        return depth;
    }

    private static bool Overlaps(Vector axis, in Box a, in Box b, Vector distance, ref double depth)
    {
        double overlap = a.Radius(axis) + b.Radius(axis) - Math.Abs(distance.Dot(axis));
        depth = Math.Min(depth, overlap);
        return overlap > 0;
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
