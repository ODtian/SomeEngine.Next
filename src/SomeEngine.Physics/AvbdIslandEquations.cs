using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

/// <summary>Minimizes a free island's common translation and rotation in its current AVBD objective.</summary>
internal sealed class AvbdIslandEquations
{
    private readonly List<Entity> _bodies = [];
    private readonly List<BodyPose> _poses = [];

    internal void Minimize<TConstraints>(PhysicsGraphData graph, in PhysicsStepContext context,
        scoped EntityQueryViews queries, scoped PhysicsBodies bodies, TConstraints constraints, float initialErrorFraction)
        where TConstraints : struct, IAvbdGraphConstraints
    {
        EntityQuery writes = queries.Get(1);
        foreach (PhysicsGraphBatch batch in graph.Batches)
        foreach (PhysicsGraphData.Island island in batch.Islands)
        {
            bool hasFixedReference = false;
            foreach (Entity body in island.Reads)
                if (!island.DynamicBodies.Contains(body)) { hasFixedReference = true; break; }
            foreach (PhysicsGraphData.ConstraintInput constraint in island.Records)
                foreach (bool writesBody in constraint.Bodies.Values)
                    if (!writesBody) { hasFixedReference = true; break; }
            if (hasFixedReference) continue;

            _bodies.Clear(); _poses.Clear();
            double mass = 0;
            foreach (Entity body in island.BodyOrder)
            {
                if (!writes.TryGetRow(body, out QueryRow row)) { hasFixedReference = true; break; }
                ref readonly BodyMass bodyMass = ref row.ReadRef<BodyMass>();
                ref readonly BodyPose pose = ref row.ReadRef<BodyPose>();
                mass += 1d / bodyMass.InverseMass;
                _bodies.Add(body); _poses.Add(pose);
            }
            if (hasFixedReference || _bodies.Count == 0) continue;

            for (int width = 2; width < _bodies.Count; width *= 2)
                for (int first = 0; first < _bodies.Count; first += width)
                {
                    int count = Math.Min(width, _bodies.Count - first);
                    if (count > 1) MinimizeTranslation(in context, queries, bodies, constraints, initialErrorFraction, first, count);
                }
            MinimizeTranslation(in context, queries, bodies, constraints, initialErrorFraction, 0, _bodies.Count);
            MinimizeRotation(in context, queries, bodies, constraints, initialErrorFraction, mass);
        }
    }

    private void MinimizeTranslation<TConstraints>(in PhysicsStepContext context, scoped EntityQueryViews queries,
        scoped PhysicsBodies bodies, TConstraints constraints, float initialErrorFraction, int first, int count)
        where TConstraints : struct, IAvbdGraphConstraints
    {
        double mass = 0, offsetX = 0, offsetY = 0, offsetZ = 0;
        double squaredDuration = context.Duration * context.Duration;
        for (int index = first; index < first + count; index++)
        {
            queries.Get(1).TryGetRow(_bodies[index], out QueryRow row);
            ref readonly BodyMass bodyMass = ref row.ReadRef<BodyMass>();
            ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
            BodyPose pose = _poses[index] = row.ReadRef<BodyPose>();
            Vector3 center = pose.GetCenterOfMass(in bodyMass);
            double value = 1d / bodyMass.InverseMass;
            mass += value;
            offsetX += value * ((double)center.X - step.PredictedCenterOfMass.X);
            offsetY += value * ((double)center.Y - step.PredictedCenterOfMass.Y);
            offsetZ += value * ((double)center.Z - step.PredictedCenterOfMass.Z);
        }
        double inertia = mass / squaredDuration;
        Vector3 inertiaGradient = new((float)(offsetX / squaredDuration), (float)(offsetY / squaredDuration), (float)(offsetZ / squaredDuration));
        Vector3 constraintGradient = ReadConstraintGradient(in context, queries, bodies, constraints, initialErrorFraction, first, count);
        Vector3 gradient = constraintGradient + inertiaGradient;
        Vector3 direction = gradient * (float)(-1 / inertia);
        if (direction == Vector3.Zero) return;

        Vector3 shiftedGradient;
        try
        {
            WritePoses(queries.Get(1), direction, first, count);
            shiftedGradient = ReadConstraintGradient(in context, queries, bodies, constraints, initialErrorFraction, first, count);
        }
        finally { WritePoses(queries.Get(1), Vector3.Zero, first, count); }

        double inertiaCurvature = inertia * Dot(direction, direction);
        double curvature = inertiaCurvature + Dot(shiftedGradient - constraintGradient, direction);
        float scale = (float)(inertiaCurvature / Math.Max(inertiaCurvature, curvature));
        WritePoses(queries.Get(1), direction * scale, first, count);
    }

    private void MinimizeRotation<TConstraints>(in PhysicsStepContext context, scoped EntityQueryViews queries,
        scoped PhysicsBodies bodies, TConstraints constraints, float initialErrorFraction, double mass)
        where TConstraints : struct, IAvbdGraphConstraints
    {
        double centerX = 0, centerY = 0, centerZ = 0;
        for (int index = 0; index < _bodies.Count; index++)
        {
            queries.Get(1).TryGetRow(_bodies[index], out QueryRow row);
            ref readonly BodyMass bodyMass = ref row.ReadRef<BodyMass>();
            BodyPose pose = _poses[index] = row.ReadRef<BodyPose>();
            Vector3 center = pose.GetCenterOfMass(in bodyMass);
            double value = 1d / bodyMass.InverseMass;
            centerX += value * center.X; centerY += value * center.Y; centerZ += value * center.Z;
        }
        Vector3 centroid = new((float)(centerX / mass), (float)(centerY / mass), (float)(centerZ / mass));
        Span<Vector3> axes = stackalloc Vector3[3];
        int count = ReadRotationAxes(queries.Get(1), axes);
        if (count == 0) return;
        axes = axes[..count];
        Span<double> curvature = stackalloc double[9];
        curvature.Clear();
        Vector3 gradient = ReadRotationGradient(in context, queries, bodies, constraints, initialErrorFraction,
            centroid, axes, curvature);
        Vector3 direction = SolveRotation(curvature, axes, gradient);
        if (direction == Vector3.Zero) return;

        Vector3 shiftedGradient;
        try
        {
            WriteRotatedPoses(queries.Get(1), centroid, direction);
            shiftedGradient = ReadRotationGradient(in context, queries, bodies, constraints, initialErrorFraction,
                centroid, axes, default);
        }
        finally { WritePoses(queries.Get(1), Vector3.Zero); }

        double inertiaCurvature = -Dot(gradient, direction);
        double measuredCurvature = Dot(shiftedGradient - gradient, direction);
        float scale = (float)(inertiaCurvature / Math.Max(inertiaCurvature, measuredCurvature));
        WriteRotatedPoses(queries.Get(1), centroid, direction * scale);
    }

    private int ReadRotationAxes(scoped EntityQuery rows, Span<Vector3> axes)
    {
        Span<Vector3> locked = stackalloc Vector3[3];
        int lockedCount = 0;
        for (int index = 0; index < _bodies.Count; index++)
        {
            rows.TryGetRow(_bodies[index], out QueryRow row);
            int mask = BodyInertia.GetRotationMask(in row.ReadRef<BodyMass>().InverseInertia);
            for (int axis = 0; axis < 3; axis++)
            {
                if ((mask & (1 << axis)) != 0) continue;
                Vector3 direction = Vector3.Transform(GetUnitAxis(axis), _poses[index].Rotation);
                for (int previous = 0; previous < lockedCount; previous++)
                    direction -= locked[previous] * Vector3.Dot(direction, locked[previous]);
                float squaredLength = direction.LengthSquared();
                if (squaredLength <= 1e-10f) continue;
                locked[lockedCount++] = direction / MathF.Sqrt(squaredLength);
                if (lockedCount == 3) return 0;
            }
        }
        int count = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 direction = GetUnitAxis(axis);
            for (int previous = 0; previous < lockedCount; previous++)
                direction -= locked[previous] * Vector3.Dot(direction, locked[previous]);
            for (int previous = 0; previous < count; previous++)
                direction -= axes[previous] * Vector3.Dot(direction, axes[previous]);
            float squaredLength = direction.LengthSquared();
            if (squaredLength > 1e-10f) axes[count++] = direction / MathF.Sqrt(squaredLength);
        }
        return count;
    }

    private Vector3 ReadRotationGradient<TConstraints>(in PhysicsStepContext context, scoped EntityQueryViews queries,
        scoped PhysicsBodies bodies, TConstraints constraints, float initialErrorFraction, Vector3 centroid,
        ReadOnlySpan<Vector3> axes, Span<double> curvature)
        where TConstraints : struct, IAvbdGraphConstraints
    {
        double x = 0, y = 0, z = 0;
        float inverseStepSquared = 1 / ((float)context.Duration * (float)context.Duration);
        foreach (Entity body in _bodies)
        {
            queries.Get(1).TryGetRow(body, out QueryRow row);
            ref readonly BodyPose pose = ref row.ReadRef<BodyPose>();
            ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
            ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
            Vector3 offset = pose.GetCenterOfMass(in mass) - centroid;
            AvbdBodyEquation equation = row.ReadRef<AvbdBodyEquation>();
            equation.Reset(in pose, in mass, in step, (float)context.Duration);
            Vector3 inertiaForce = equation.LinearGradient;
            constraints.AccumulateBodyEquation(in context, queries, 3, body, bodies, initialErrorFraction, ref equation);
            Vector3 gradient = equation.AngularGradient + Vector3.Cross(offset, equation.LinearGradient);
            x += gradient.X; y += gradient.Y; z += gradient.Z;
            if (curvature.IsEmpty) continue;
            Matrix4x4 inertia = BodyInertia.InvertTensor(mass.InverseInertia);
            float geometricCurvature = offset.Length() * inertiaForce.Length();
            for (int column = 0; column < axes.Length; column++)
            {
                Vector3 axis = axes[column];
                Vector3 response = (BodyInertia.Apply(pose.Rotation, inertia, axis)
                    + (axis * offset.LengthSquared() - offset * Vector3.Dot(offset, axis)) / mass.InverseMass)
                    * inverseStepSquared + axis * geometricCurvature;
                for (int rowIndex = column; rowIndex < axes.Length; rowIndex++)
                    curvature[rowIndex * 3 + column] += Dot(axes[rowIndex], response);
            }
        }
        return new((float)x, (float)y, (float)z);
    }

    private static Vector3 SolveRotation(Span<double> matrix, ReadOnlySpan<Vector3> axes, Vector3 gradient)
    {
        int count = axes.Length;
        for (int row = 0; row < count; row++)
        {
            for (int column = 0; column < row; column++)
            {
                double value = matrix[row * 3 + column];
                for (int previous = 0; previous < column; previous++)
                    value -= matrix[row * 3 + previous] * matrix[column * 3 + previous] * matrix[previous * 3 + previous];
                matrix[row * 3 + column] = value / matrix[column * 3 + column];
            }
            double diagonal = matrix[row * 3 + row];
            for (int previous = 0; previous < row; previous++)
                diagonal -= matrix[row * 3 + previous] * matrix[row * 3 + previous] * matrix[previous * 3 + previous];
            matrix[row * 3 + row] = diagonal;
        }
        Span<double> response = stackalloc double[3];
        for (int row = 0; row < count; row++)
        {
            response[row] = -Dot(axes[row], gradient);
            for (int column = 0; column < row; column++) response[row] -= matrix[row * 3 + column] * response[column];
        }
        for (int row = 0; row < count; row++) response[row] /= matrix[row * 3 + row];
        for (int row = count - 1; row >= 0; row--)
            for (int column = row + 1; column < count; column++) response[row] -= matrix[column * 3 + row] * response[column];
        Vector3 rotation = default;
        for (int row = 0; row < count; row++) rotation += axes[row] * (float)response[row];
        return rotation;
    }

    private Vector3 ReadConstraintGradient<TConstraints>(in PhysicsStepContext context, scoped EntityQueryViews queries,
        scoped PhysicsBodies bodies, TConstraints constraints, float initialErrorFraction, int first, int count)
        where TConstraints : struct, IAvbdGraphConstraints
    {
        double x = 0, y = 0, z = 0;
        for (int index = first; index < first + count; index++)
        {
            Entity body = _bodies[index];
            queries.Get(1).TryGetRow(body, out QueryRow row);
            ref readonly BodyPose pose = ref row.ReadRef<BodyPose>();
            ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
            ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
            AvbdBodyEquation equation = row.ReadRef<AvbdBodyEquation>();
            equation.Reset(in pose, in mass, in step, (float)context.Duration);
            Vector3 inertia = equation.LinearGradient;
            constraints.AccumulateBodyEquation(in context, queries, 3, body, bodies, initialErrorFraction, ref equation);
            x += (double)equation.LinearGradient.X - inertia.X;
            y += (double)equation.LinearGradient.Y - inertia.Y;
            z += (double)equation.LinearGradient.Z - inertia.Z;
        }
        return new((float)x, (float)y, (float)z);
    }

    private void WritePoses(scoped EntityQuery rows, Vector3 translation) => WritePoses(rows, translation, 0, _bodies.Count);

    private void WritePoses(scoped EntityQuery rows, Vector3 translation, int first, int count)
    {
        for (int index = first; index < first + count; index++)
        {
            rows.TryGetRow(_bodies[index], out QueryRow row);
            BodyPose pose = _poses[index];
            pose.Position += translation;
            row.ReadWrite<BodyPose>() = pose;
        }
    }

    private void WriteRotatedPoses(scoped EntityQuery rows, Vector3 centroid, Vector3 rotation)
    {
        float angle = rotation.Length();
        Quaternion change = Quaternion.CreateFromAxisAngle(rotation / angle, angle);
        for (int index = 0; index < _bodies.Count; index++)
        {
            rows.TryGetRow(_bodies[index], out QueryRow row);
            ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
            BodyPose pose = _poses[index];
            Vector3 center = centroid + Vector3.Transform(pose.GetCenterOfMass(in mass) - centroid, change);
            pose.Rotation = Quaternion.Normalize(change * pose.Rotation);
            pose.SetCenterOfMass(center, in mass);
            row.ReadWrite<BodyPose>() = pose;
        }
    }

    private static Vector3 GetUnitAxis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
    private static double Dot(Vector3 first, Vector3 second) =>
        (double)first.X * second.X + (double)first.Y * second.Y + (double)first.Z * second.Z;
}
