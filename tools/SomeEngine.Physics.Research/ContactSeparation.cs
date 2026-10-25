using System.Runtime.InteropServices;

namespace SomeEngine.Physics.Research;

// Correct geometric overlap after velocity reconstruction. The displacement solve
// keeps the velocity and contact-force solve separate from contact error recovery.
internal sealed unsafe class ContactSeparation : IDisposable
{
    private readonly DemoLibrary library;
    private readonly nint demo;
    private readonly BodyData* bodies;
    private readonly double[] inverseMass, changes;
    private double[] matrix = [], factor = [], right = [], fixedValues = [], solution = [];
    private bool[] active = [];
    private ConstraintData* contacts;
    private int count, bandwidth, stride;

    public ContactSeparation(DemoLibrary library, nint demo)
    {
        this.library = library; this.demo = demo;
        int bodyCount = library.BodyCount(demo);
        bodies = (BodyData*)NativeMemory.Alloc((nuint)bodyCount, (nuint)sizeof(BodyData));
        inverseMass = new double[bodyCount];
        changes = new double[bodyCount * 6];
    }

    public void Separate()
    {
        for (int pass = 0; pass < inverseMass.Length; pass++)
        {
            ConstraintData* output;
            count = library.ReadContacts(demo, bodies, &output);
            contacts = output;
            if (count == 0) return;
            double penetration = 0;
            for (int i = 0; i < count; i++) penetration = Math.Max(penetration, -contacts[i].Error);
            if (penetration <= 0.000001) return;
            BuildSystem();
            for (int iteration = 0; iteration <= count; iteration++)
            {
                BandedCholesky.Solve(count, bandwidth, matrix, factor, active, right, fixedValues, solution);
                bool changed = false;
                for (int i = 0; i < count; i++)
                {
                    if (active[i] && solution[i] > 0) { active[i] = false; changed = true; }
                    else if (!active[i])
                    {
                        double gap = right[i];
                        for (int j = Math.Max(0, i - bandwidth); j < Math.Min(count, i + bandwidth + 1); j++)
                            gap -= matrix[Math.Max(i, j) * stride + Math.Abs(i - j)] * solution[j];
                        if (gap < 0) { active[i] = true; changed = true; }
                    }
                }
                if (!changed) break;
            }
            Array.Clear(changes);
            for (int i = 0; i < count; i++)
            {
                if (!active[i]) continue;
                ConstraintData* row = contacts + i;
                for (int axis = 0; axis < library.Dimension; axis++)
                {
                    changes[row->BodyA * 6 + axis] -= inverseMass[row->BodyA] * row->JacobianA[axis] * solution[i];
                    changes[row->BodyB * 6 + axis] -= inverseMass[row->BodyB] * row->JacobianB[axis] * solution[i];
                }
            }
            fixed (double* displacement = changes) library.Apply(demo, displacement, null, 0);
        }
    }

    private void BuildSystem()
    {
        for (int body = 0; body < inverseMass.Length; body++)
            inverseMass[body] = bodies[body].Inertia[0] > 0 ? 1 / bodies[body].Inertia[0] : 0;
        bandwidth = 0;
        for (int i = 0; i < count; i++)
            for (int j = 0; j < i; j++)
                if (Coefficient(contacts + i, contacts + j) != 0) bandwidth = Math.Max(bandwidth, i - j);
        stride = bandwidth + 1;
        if (matrix.Length < count * stride) { matrix = new double[count * stride]; factor = new double[count * stride]; }
        if (right.Length < count)
        {
            right = new double[count]; fixedValues = new double[count]; solution = new double[count]; active = new bool[count];
        }
        Array.Clear(matrix);
        for (int i = 0; i < count; i++)
        {
            right[i] = contacts[i].Error;
            active[i] = true;
            for (int j = Math.Max(0, i - bandwidth); j <= i; j++)
                matrix[i * stride + i - j] = Coefficient(contacts + i, contacts + j);
            matrix[i * stride] *= 1.00000001;
        }
    }

    private double Coefficient(ConstraintData* a, ConstraintData* b)
    {
        double value = 0;
        for (int axis = 0; axis < library.Dimension; axis++)
        {
            if (a->BodyA == b->BodyA) value += inverseMass[a->BodyA] * a->JacobianA[axis] * b->JacobianA[axis];
            if (a->BodyA == b->BodyB) value += inverseMass[a->BodyA] * a->JacobianA[axis] * b->JacobianB[axis];
            if (a->BodyB == b->BodyA) value += inverseMass[a->BodyB] * a->JacobianB[axis] * b->JacobianA[axis];
            if (a->BodyB == b->BodyB) value += inverseMass[a->BodyB] * a->JacobianB[axis] * b->JacobianB[axis];
        }
        return value;
    }

    public void Dispose() => NativeMemory.Free(bodies);
}
