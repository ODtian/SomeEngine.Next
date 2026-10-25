using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Physics.Research;

// The coarse solve transports original joint/contact forces across the full connected chain.
// Its unknowns are the existing multipliers, so friction consumes the same contact load.
internal sealed unsafe class CoupledCorrection : IDisposable
{
    private readonly DemoLibrary library;
    private readonly nint demo;
    private readonly int bodyCount, degrees, passes;
    private readonly BodyData* bodies;
    private readonly double[] inverseHessian, inverseRotation, gradient, weightedForces, changes;
    private readonly int[] bodyCounts, bodyStarts;
    private readonly bool[] jointBodies;
    private int[] bodyRows = [], bodySides = [];
    private int[] fineStarts = [], fineEnds = [];
    private double[] matrix = [], factor = [], right = [], solution = [], forces = [], fixedChange = [];
    private double[] fineForces = [], weights = [];
    private double[] weightedJacobians = [];
    private bool[] active = [];
    private ConstraintData* rows, fineRows;
    private int fineCount, rowCapacity;
    private int rowCount, bandwidth, stride;

    public CoupledCorrection(DemoLibrary library, nint demo, int passes)
    {
        this.library = library;
        this.demo = demo;
        this.passes = passes;
        bodyCount = library.BodyCount(demo);
        degrees = library.Dimension == 2 ? 3 : 6;
        bodies = (BodyData*)NativeMemory.AllocZeroed((nuint)bodyCount, (nuint)sizeof(BodyData));
        inverseHessian = new double[bodyCount * 6];
        inverseRotation = new double[bodyCount * 9];
        gradient = new double[bodyCount * 6];
        weightedForces = new double[bodyCount * 6];
        changes = new double[bodyCount * 6];
        bodyCounts = new int[bodyCount];
        bodyStarts = new int[bodyCount + 1];
        jointBodies = new bool[bodyCount];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void Correct(nint context) => ((CoupledCorrection)GCHandle.FromIntPtr(context).Target!).Solve();

    private void Solve()
    {
        for (int pass = 0; pass < passes; pass++)
        {
            ConstraintData* exported;
            fineCount = library.Read(demo, bodies, &exported, 0.99f);
            fineRows = exported;
            if (fineCount == 0) return;
            ResizeRows();
            BuildCoarseRows();
            BuildSystem();
            for (int i = 0; i < rowCount; i++)
            {
                active[i] = rows[i].Kind == 0 || rows[rows[i].NormalRow].Force < 0 || rows[rows[i].NormalRow].Error < 0;
                fixedChange[i] = active[i] ? 0 : -rows[i].Force;
            }
            for (int contactIteration = 0; contactIteration < 3; contactIteration++)
            {
                SolveMultipliers();
                bool changed = ProjectForces();
                if (!changed) break;
            }
            ApplyChanges();
        }
    }

    private void ResizeRows()
    {
        if (rowCapacity >= fineCount) return;
        int size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)fineCount);
        right = new double[size]; solution = new double[size]; forces = new double[size];
        fixedChange = new double[size]; active = new bool[size];
        bodyRows = new int[size * 2]; bodySides = new int[size * 2];
        weightedJacobians = new double[size * 12];
        fineStarts = new int[size]; fineEnds = new int[size];
        fineForces = new double[size]; weights = new double[size];
        NativeMemory.Free(rows);
        rows = (ConstraintData*)NativeMemory.Alloc((nuint)size, (nuint)sizeof(ConstraintData));
        rowCapacity = size;
    }

    private void BuildCoarseRows()
    {
        rowCount = 0;
        Array.Clear(jointBodies);
        for (int i = 0; i < fineCount;)
        {
            int start = i;
            ConstraintData* first = fineRows + i;
            fineStarts[rowCount] = start;
            if (first->Kind == 0)
            {
                rows[rowCount] = *first;
                if (first->BodyA >= 0) jointBodies[first->BodyA] = true;
                if (first->BodyB >= 0) jointBodies[first->BodyB] = true;
                i++;
            }
            else
            {
                while (i < fineCount && fineRows[i].Kind != 0 && fineRows[i].BodyA == first->BodyA && fineRows[i].BodyB == first->BodyB) i++;
                ConstraintData* row = rows + rowCount;
                *row = default;
                row->BodyA = first->BodyA; row->BodyB = first->BodyB;
                row->Kind = 1; row->NormalRow = rowCount;
                int normals = 0;
                for (int j = start; j < i; j++)
                {
                    if (fineRows[j].Kind != 1) continue;
                    row->Force += fineRows[j].Force;
                    normals++;
                }
                for (int j = start; j < i; j++)
                {
                    if (fineRows[j].Kind != 1) continue;
                    double weight = row->Force < 0 ? fineRows[j].Force / row->Force : 1.0 / normals;
                    weights[j] = weight;
                    row->Error += weight * fineRows[j].Error;
                    for (int axis = 0; axis < degrees; axis++)
                    {
                        row->JacobianA[axis] += weight * fineRows[j].JacobianA[axis];
                        row->JacobianB[axis] += weight * fineRows[j].JacobianB[axis];
                    }
                }
                if (Math.Abs(row->JacobianA[library.Dimension - 1]) < 0.5) continue;
            }
            fineEnds[rowCount] = i;
            rowCount++;
        }
    }

    private void BuildSystem()
    {
        Array.Clear(bodyCounts);
        for (int body = 0; body < bodyCount; body++)
        {
            for (int axis = 0; axis < degrees; axis++)
            {
                int index = body * 6 + axis;
                double inertia = bodies[body].Inertia[axis];
                bool moves = jointBodies[body] || axis == library.Dimension - 1;
                double curvature = library.Dimension == 2 && axis == 2 ? bodies[body].Curvature[0] : 0;
                inverseHessian[index] = inertia > 0 && moves ? 1 / (inertia + curvature) : 0;
                gradient[index] = inertia * bodies[body].Difference[axis];
            }
            if (library.Dimension == 3 && jointBodies[body] && bodies[body].Inertia[0] > 0)
            {
                AngularMatrix.Invert(bodies[body].Curvature, bodies[body].Inertia + 3, inverseRotation.AsSpan(body * 9, 9));
                for (int row = 0; row < 3; row++)
                    for (int column = 0; column < 3; column++)
                        if (row != column) gradient[body * 6 + row + 3] += bodies[body].Curvature[row * 3 + column] * bodies[body].Difference[column + 3];
            }
        }
        for (int i = 0; i < fineCount; i++)
        {
            ConstraintData* row = fineRows + i;
            if (row->BodyA >= 0) AddForce(row->BodyA, row->JacobianA, row->Force);
            if (row->BodyB >= 0) AddForce(row->BodyB, row->JacobianB, row->Force);
        }
        for (int i = 0; i < rowCount; i++)
        {
            ConstraintData* row = rows + i;
            if (row->BodyA >= 0) bodyCounts[row->BodyA]++;
            if (row->BodyB >= 0) bodyCounts[row->BodyB]++;
        }
        for (int body = 0; body < bodyCount; body++)
        {
            for (int axis = 0; axis < 3; axis++) weightedForces[body * 6 + axis] = inverseHessian[body * 6 + axis] * gradient[body * 6 + axis];
            if (library.Dimension == 3 && jointBodies[body])
                for (int row = 0; row < 3; row++)
                {
                    double value = 0;
                    for (int column = 0; column < 3; column++) value += inverseRotation[body * 9 + row * 3 + column] * gradient[body * 6 + column + 3];
                    weightedForces[body * 6 + row + 3] = value;
                }
        }
        bodyStarts[0] = 0;
        for (int body = 0; body < bodyCount; body++) bodyStarts[body + 1] = bodyStarts[body] + bodyCounts[body];
        Array.Clear(bodyCounts);
        for (int i = 0; i < rowCount; i++)
        {
            ConstraintData* row = rows + i;
            if (row->BodyA >= 0) AddIncidence(row->BodyA, i, 0);
            if (row->BodyB >= 0) AddIncidence(row->BodyB, i, 1);
            right[i] = row->Error;
            if (row->BodyA >= 0) right[i] -= WeightedGradient(row->BodyA, row->JacobianA);
            if (row->BodyB >= 0) right[i] -= WeightedGradient(row->BodyB, row->JacobianB);
        }
        bandwidth = 0;
        for (int body = 0; body < bodyCount; body++)
        {
            if (bodyCounts[body] == 0 || bodies[body].Inertia[0] == 0) continue;
            bandwidth = Math.Max(bandwidth, bodyRows[bodyStarts[body + 1] - 1] - bodyRows[bodyStarts[body]]);
        }
        stride = bandwidth + 1;
        int length = rowCount * stride;
        if (matrix.Length < length) { matrix = new double[length]; factor = new double[length]; }
        Array.Clear(matrix, 0, length);
        for (int body = 0; body < bodyCount; body++)
        {
            if (bodies[body].Inertia[0] == 0) continue;
            for (int a = bodyStarts[body]; a < bodyStarts[body + 1]; a++)
            {
                int i = bodyRows[a];
                double* jacobianI = bodySides[a] == 0 ? rows[i].JacobianA : rows[i].JacobianB;
                for (int axis = 0; axis < 3; axis++) weightedJacobians[a * 6 + axis] = jacobianI[axis] * inverseHessian[body * 6 + axis];
                if (library.Dimension == 3 && jointBodies[body])
                    for (int row = 0; row < 3; row++)
                    {
                        double value = 0;
                        for (int column = 0; column < 3; column++) value += inverseRotation[body * 9 + row * 3 + column] * jacobianI[column + 3];
                        weightedJacobians[a * 6 + row + 3] = value;
                    }
                for (int b = bodyStarts[body]; b <= a; b++)
                {
                    int j = bodyRows[b];
                    double* jacobianJ = bodySides[b] == 0 ? rows[j].JacobianA : rows[j].JacobianB;
                    double value = 0;
                    for (int axis = 0; axis < degrees; axis++) value += weightedJacobians[a * 6 + axis] * jacobianJ[axis];
                    matrix[i * stride + i - j] += value;
                }
            }
        }
        // Contact patches have dependent force rows. This small relative compliance
        // selects a force distribution while retaining the full patch and friction torque.
        for (int i = 0; i < rowCount; i++) matrix[i * stride] *= 1.00000001;
    }

    private void AddIncidence(int body, int row, int side)
    {
        int index = bodyStarts[body] + bodyCounts[body]++;
        bodyRows[index] = row; bodySides[index] = side;
    }

    private void AddForce(int body, double* jacobian, double force)
    {
        for (int axis = 0; axis < degrees; axis++) gradient[body * 6 + axis] += jacobian[axis] * force;
    }

    private double WeightedGradient(int body, double* jacobian)
    {
        double value = 0;
        for (int axis = 0; axis < degrees; axis++) value += jacobian[axis] * weightedForces[body * 6 + axis];
        return value;
    }

    private void SolveMultipliers()
    {
        BandedCholesky.Solve(rowCount, bandwidth, matrix, factor, active, right, fixedChange, solution);
        for (int i = 0; i < rowCount; i++) forces[i] = rows[i].Force + solution[i];
    }

    private bool ProjectForces()
    {
        bool changed = false;
        for (int i = 0; i < rowCount; i++)
        {
            if (rows[i].Kind != 1) continue;
            if (forces[i] > 0)
            {
                forces[i] = 0;
                if (active[i]) changed = true;
                active[i] = false;
                fixedChange[i] = -rows[i].Force;
            }
        }
        return changed;
    }

    private void ApplyChanges()
    {
        Array.Copy(gradient, changes, gradient.Length);
        for (int i = 0; i < rowCount; i++)
        {
            ConstraintData* row = rows + i;
            double difference = forces[i] - row->Force;
            for (int axis = 0; axis < degrees; axis++)
            {
                if (row->BodyA >= 0) changes[row->BodyA * 6 + axis] += row->JacobianA[axis] * difference;
                if (row->BodyB >= 0) changes[row->BodyB * 6 + axis] += row->JacobianB[axis] * difference;
            }
        }
        for (int body = 0; body < bodyCount; body++)
        {
            for (int axis = 0; axis < 3; axis++) changes[body * 6 + axis] *= -inverseHessian[body * 6 + axis];
            if (library.Dimension == 3)
            {
                double x = changes[body * 6 + 3], y = changes[body * 6 + 4], z = changes[body * 6 + 5];
                for (int axis = 0; axis < 3; axis++)
                    changes[body * 6 + axis + 3] = -(inverseRotation[body * 9 + axis * 3] * x
                        + inverseRotation[body * 9 + axis * 3 + 1] * y + inverseRotation[body * 9 + axis * 3 + 2] * z);
            }
        }
        for (int i = 0; i < fineCount; i++) fineForces[i] = fineRows[i].Force;
        for (int i = 0; i < rowCount; i++)
        {
            if (rows[i].Kind == 0) fineForces[fineStarts[i]] = forces[i];
            else
                for (int j = fineStarts[i]; j < fineEnds[i]; j++)
                    if (fineRows[j].Kind == 1) fineForces[j] = forces[i] * weights[j];
        }
        for (int i = 0; i < fineCount; i++)
        {
            if (fineRows[i].Kind != 1) continue;
            double bound = -fineForces[i] * fineRows[i].Friction;
            double squared = 0;
            for (int k = 1; k < library.Dimension; k++) squared += fineForces[i + k] * fineForces[i + k];
            if (squared <= bound * bound) continue;
            double scale = bound / Math.Sqrt(squared);
            for (int k = 1; k < library.Dimension; k++) fineForces[i + k] *= scale;
        }
        fixed (double* displacement = changes, multipliers = fineForces) library.Apply(demo, displacement, multipliers, fineCount);
    }

    public void Dispose()
    {
        NativeMemory.Free(bodies);
        NativeMemory.Free(rows);
    }
}
