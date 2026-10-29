using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

internal sealed partial class ContactSupportData
{
    internal void ReadActivityErrors(scoped PhysicsQueryRows workBodies, scoped PhysicsQueryRows contacts,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, ref PhysicsActivityErrors errors)
    {
        foreach (QueryRow row in workBodies.Rows)
        {
            ref readonly LongRangeBodySupport support = ref row.ReadRef<LongRangeBodySupport>();
            if (!support.Enabled) continue;
            float height = Vector3.Dot(bodies.ReadPose(row.Entity).GetCenterOfMass(in bodies.ReadMass(row.Entity)), _settings.UpDirection);
            errors.Add(row.Entity, new PhysicsConstraintError(MathF.Max(0, support.MinimumHeight - height), 0));
        }
        foreach (QueryRow row in contacts.Rows)
        {
            ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
            ref readonly LongRangeContactSupport support = ref row.ReadRef<LongRangeContactSupport>();
            ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
            PhysicsConstraintError error = ContactBodyMath.GetActivityError(in point, row, bodies, in context);
            if (support.Enabled)
            {
                float height = Vector3.Dot(SidePoint(in point, links, support.UpperIsSecond, bodies, in context, false), _settings.UpDirection);
                error = error with { Linear = MathF.Max(error.Linear, support.MinimumHeight - height) };
            }
            foreach (ref readonly ContactBodyLink link in links) errors.Add(ContactBodyMath.GetBody(in link, bodies, in context), in error);
        }
    }

    internal void Correct(scoped PhysicsQueryRows workBodies, scoped PhysicsQueryRows contacts, Entity body, scoped PhysicsBodies bodies, in PhysicsStepContext context,
        in AvbdBodyEquation factor, ref Vector3 translation, ref Vector3 rotation)
    {
        if (!workBodies.TryGetRow(body, out QueryRow bodyRow)) return;
        ref LongRangeBodySupport center = ref bodyRow.ReadWrite<LongRangeBodySupport>();
        center.Reaction = 0;
        DynamicBuffer<LongRangeSupportEquation> buffer = bodyRow.Buffer<LongRangeSupportEquation>();
        buffer.Clear();
        if (center.Enabled)
        {
            float height = Vector3.Dot(bodies.ReadPose(body).GetCenterOfMass(in bodies.ReadMass(body)), _settings.UpDirection);
            buffer.Add(new LongRangeSupportEquation
            {
                LinearGradient = _settings.UpDirection,
                HeightError = center.MinimumHeight - _settings.InitialErrorFraction * center.InitialError - height
                    - Vector3.Dot(_settings.UpDirection, translation),
            });
        }
        if (_byBody.TryGetValue(body, out List<Entity>? points))
            foreach (Entity pointEntity in points)
            {
                contacts.TryGetRow(pointEntity, out QueryRow row);
                ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
                ref LongRangeContactSupport state = ref row.ReadWrite<LongRangeContactSupport>();
                state.Reaction = 0;
                ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
                ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, _settings.UpDirection,
                    out Vector3 linear, out Vector3 angular, normal: true);
                float side = state.UpperIsSecond ? 1 : -1;
                linear *= side; angular *= side;
                float height = Vector3.Dot(SidePoint(in point, links, state.UpperIsSecond, bodies, in context, false), _settings.UpDirection);
                buffer.Add(new LongRangeSupportEquation
                {
                    Contact = pointEntity, LinearGradient = linear, AngularGradient = angular,
                    HeightError = state.MinimumHeight - _settings.InitialErrorFraction * state.InitialError - height
                        - Vector3.Dot(linear, translation) - Vector3.Dot(angular, rotation),
                });
            }
        Span<LongRangeSupportEquation> equations = buffer.AsSpan();
        bool hasError = false;
        foreach (ref readonly LongRangeSupportEquation equation in equations) hasError |= equation.HeightError > 0;
        if (!hasError) return;
        factor.SolveResponse(_settings.UpDirection, Vector3.Zero, out Vector3 heightLinear, out Vector3 heightAngular);
        Span<int> active = stackalloc int[3];
        if (center.Enabled)
        {
            equations[0].TranslationResponse = heightLinear;
            equations[0].RotationResponse = heightAngular;
            active[0] = 0;
        }
        if (!center.Enabled || !SolveForces(equations, active[..1]))
        {
            factor.SolveResponse(Vector3.Zero, _horizontalX, out Vector3 firstLinear, out Vector3 firstAngular);
            factor.SolveResponse(Vector3.Zero, _horizontalY, out Vector3 secondLinear, out Vector3 secondAngular);
            foreach (ref LongRangeSupportEquation equation in equations)
            {
                float first = Vector3.Dot(equation.AngularGradient, _horizontalX);
                float second = Vector3.Dot(equation.AngularGradient, _horizontalY);
                equation.TranslationResponse = heightLinear + firstLinear * first + secondLinear * second;
                equation.RotationResponse = heightAngular + firstAngular * first + secondAngular * second;
            }
            FindForces(equations, active);
        }
        foreach (ref readonly LongRangeSupportEquation equation in equations)
        {
            translation += equation.TranslationResponse * equation.Reaction;
            rotation += equation.RotationResponse * equation.Reaction;
            if (equation.Contact == Entity.Null) center.Reaction = equation.Reaction;
            else
            {
                contacts.TryGetRow(equation.Contact, out QueryRow row);
                row.ReadWrite<LongRangeContactSupport>().Reaction = equation.Reaction;
            }
        }
    }

    private void FindForces(Span<LongRangeSupportEquation> equations, Span<int> active)
    {
        for (int first = 0; first < equations.Length; first++)
        {
            active[0] = first;
            if (SolveForces(equations, active[..1])) return;
        }
        for (int first = 0; first < equations.Length; first++)
            for (int second = first + 1; second < equations.Length; second++)
            {
                active[0] = first; active[1] = second;
                if (SolveForces(equations, active[..2])) return;
            }
        for (int first = 0; first < equations.Length; first++)
            for (int second = first + 1; second < equations.Length; second++)
                for (int third = second + 1; third < equations.Length; third++)
                {
                    active[0] = first; active[1] = second; active[2] = third;
                    if (SolveForces(equations, active)) return;
                }
    }

    private bool SolveForces(Span<LongRangeSupportEquation> equations, ReadOnlySpan<int> active)
    {
        Span<double> coefficients = stackalloc double[12];
        double scale = 0;
        int count = active.Length;
        for (int row = 0; row < count; row++)
        {
            for (int column = 0; column < count; column++)
                coefficients[row * 4 + column] = Response(in equations[active[row]], in equations[active[column]]);
            coefficients[row * 4 + count] = equations[active[row]].HeightError;
            scale = Math.Max(scale, coefficients[row * 4 + row]);
        }
        for (int column = 0; column < count; column++)
        {
            double diagonal = coefficients[column * 4 + column];
            if (diagonal <= scale * _settings.RelativePivotTolerance) return false;
            for (int row = column + 1; row < count; row++)
            {
                double ratio = coefficients[row * 4 + column] / diagonal;
                for (int component = column + 1; component <= count; component++)
                    coefficients[row * 4 + component] -= ratio * coefficients[column * 4 + component];
            }
        }
        Span<double> forces = stackalloc double[3];
        for (int row = count - 1; row >= 0; row--)
        {
            double value = coefficients[row * 4 + count];
            for (int column = row + 1; column < count; column++) value -= coefficients[row * 4 + column] * forces[column];
            forces[row] = value / coefficients[row * 4 + row];
            if (forces[row] < 0) return false;
        }
        foreach (ref readonly LongRangeSupportEquation equation in equations)
        {
            double height = 0;
            for (int index = 0; index < count; index++) height += Response(in equation, in equations[active[index]]) * forces[index];
            if (equation.HeightError > height + _settings.DistanceTolerance) return false;
        }
        for (int index = 0; index < count; index++) equations[active[index]].Reaction = (float)forces[index];
        return true;
    }

    private static double Response(in LongRangeSupportEquation height, in LongRangeSupportEquation force) =>
        Vector3.Dot(height.LinearGradient, force.TranslationResponse) + Vector3.Dot(height.AngularGradient, force.RotationResponse);

    internal void Update(scoped PhysicsQueryRows workBodies, scoped PhysicsQueryRows contacts, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        if (UpdateMotion(workBodies, bodies)) Build(workBodies, contacts, bodies, in context);
        foreach (QueryRow row in contacts.Rows)
        {
            ref LongRangeContactSupport state = ref row.ReadWrite<LongRangeContactSupport>();
            state.Load = state.Reaction;
        }
        foreach (QueryRow row in workBodies.Rows)
        {
            ref readonly LongRangeBodySupport state = ref row.ReadRef<LongRangeBodySupport>();
            if (!state.Enabled) continue;
            foreach (ref readonly LongRangeSupportLoad load in row.ReadBuffer<LongRangeSupportLoad>().AsSpan())
            {
                contacts.TryGetRow(load.Contact, out QueryRow point);
                point.ReadWrite<LongRangeContactSupport>().Load += state.Reaction * load.Weight;
            }
        }
        for (int index = _order.Count - 1; index >= 0; index--)
        {
            contacts.TryGetRow(_order[index], out QueryRow row);
            ref LongRangeContactSupport state = ref row.ReadWrite<LongRangeContactSupport>();
            ref ContactResponse response = ref row.ReadWrite<ContactResponse>();
            ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
            ref readonly ContactMaterial material = ref row.ReadRef<ContactMaterial>();
            Vector3 original = response.ForceOnSecond - state.ExtraForceOnSecond;
            Vector3 total = original + _settings.UpDirection * (state.Load * (state.UpperIsSecond ? 1 : -1));
            float pressure = MathF.Max(0, Vector3.Dot(total, point.Normal));
            Vector3 tangent = total - point.Normal * Vector3.Dot(total, point.Normal);
            float magnitude = tangent.Length(), bound = material.StaticFriction * pressure;
            if (magnitude > bound) tangent *= bound / magnitude;
            total = point.Normal * pressure + tangent;
            state.ExtraForceOnSecond = total - original;
            response.ForceOnSecond = total;
            foreach (ref readonly LongRangeSupportLoad load in row.ReadBuffer<LongRangeSupportLoad>().AsSpan())
            {
                contacts.TryGetRow(load.Contact, out QueryRow below);
                below.ReadWrite<LongRangeContactSupport>().Load += state.Load * load.Weight;
            }
        }
    }
}
