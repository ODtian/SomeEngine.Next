namespace SomeEngine.Physics.Research;

internal static unsafe class AngularMatrix
{
    // Project the symmetric geometric curvature onto its positive eigenspace.
    // Keeping eigenvectors preserves rotations about a freely twisting bone.
    public static void Invert(double* curvature, double* inertia, Span<double> inverse)
    {
        Span<double> values = stackalloc double[9];
        Span<double> vectors = stackalloc double[9];
        vectors.Clear();
        for (int row = 0; row < 3; row++)
        {
            vectors[row * 3 + row] = 1;
            for (int column = 0; column < 3; column++) values[row * 3 + column] = 0.5 * (curvature[row * 3 + column] + curvature[column * 3 + row]);
        }
        Span<double> hessian = stackalloc double[9];
        values.CopyTo(hessian);
        for (int axis = 0; axis < 3; axis++) hessian[axis * 3 + axis] += inertia[axis];
        double leadingMinor = hessian[0] * hessian[4] - hessian[1] * hessian[1];
        double determinant = Determinant(hessian);
        if (hessian[0] > 0 && leadingMinor > 0 && determinant > 0)
        {
            InvertSymmetric(hessian, determinant, inverse);
            return;
        }
        for (int sweep = 0; sweep < 4; sweep++)
        {
            Rotate(values, vectors, 0, 1);
            Rotate(values, vectors, 0, 2);
            Rotate(values, vectors, 1, 2);
        }
        hessian.Clear();
        for (int axis = 0; axis < 3; axis++)
        {
            double value = Math.Max(0, values[axis * 3 + axis]);
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    hessian[row * 3 + column] += vectors[row * 3 + axis] * value * vectors[column * 3 + axis];
        }
        for (int axis = 0; axis < 3; axis++) hessian[axis * 3 + axis] += inertia[axis];
        InvertSymmetric(hessian, Determinant(hessian), inverse);
    }

    private static double Determinant(ReadOnlySpan<double> matrix) =>
        matrix[0] * (matrix[4] * matrix[8] - matrix[5] * matrix[5])
        - matrix[1] * (matrix[1] * matrix[8] - matrix[2] * matrix[5])
        + matrix[2] * (matrix[1] * matrix[5] - matrix[2] * matrix[4]);

    private static void InvertSymmetric(ReadOnlySpan<double> hessian, double determinant, Span<double> inverse)
    {
        double a = hessian[0], b = hessian[1], c = hessian[2], d = hessian[4], e = hessian[5], f = hessian[8];
        inverse[0] = (d * f - e * e) / determinant;
        inverse[1] = inverse[3] = (c * e - b * f) / determinant;
        inverse[2] = inverse[6] = (b * e - c * d) / determinant;
        inverse[4] = (a * f - c * c) / determinant;
        inverse[5] = inverse[7] = (b * c - a * e) / determinant;
        inverse[8] = (a * d - b * b) / determinant;
    }

    private static void Rotate(Span<double> values, Span<double> vectors, int p, int q)
    {
        double offDiagonal = values[p * 3 + q];
        if (offDiagonal == 0) return;
        double ratio = (values[q * 3 + q] - values[p * 3 + p]) / (2 * offDiagonal);
        double tangent = Math.CopySign(1, ratio) / (Math.Abs(ratio) + Math.Sqrt(1 + ratio * ratio));
        double cosine = 1 / Math.Sqrt(1 + tangent * tangent), sine = tangent * cosine;
        for (int row = 0; row < 3; row++)
        {
            double x = values[row * 3 + p], y = values[row * 3 + q];
            values[row * 3 + p] = cosine * x - sine * y;
            values[row * 3 + q] = sine * x + cosine * y;
            x = vectors[row * 3 + p]; y = vectors[row * 3 + q];
            vectors[row * 3 + p] = cosine * x - sine * y;
            vectors[row * 3 + q] = sine * x + cosine * y;
        }
        for (int column = 0; column < 3; column++)
        {
            double x = values[p * 3 + column], y = values[q * 3 + column];
            values[p * 3 + column] = cosine * x - sine * y;
            values[q * 3 + column] = sine * x + cosine * y;
        }
    }
}
