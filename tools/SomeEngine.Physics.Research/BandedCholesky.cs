namespace SomeEngine.Physics.Research;

internal static class BandedCholesky
{
    public static void Solve(int count, int bandwidth, double[] matrix, double[] factor,
        bool[] active, double[] right, double[] fixedValues, double[] solution)
    {
        int stride = bandwidth + 1;
        Array.Clear(factor, 0, count * stride);
        for (int i = 0; i < count; i++)
        {
            solution[i] = active[i] ? right[i] : fixedValues[i];
            if (active[i])
                for (int j = Math.Max(0, i - bandwidth); j < Math.Min(count, i + bandwidth + 1); j++)
                    if (!active[j]) solution[i] -= matrix[Math.Max(i, j) * stride + Math.Abs(i - j)] * fixedValues[j];
            for (int j = Math.Max(0, i - bandwidth); j <= i; j++)
            {
                int address = i * stride + i - j;
                if (!active[i] || !active[j]) { factor[address] = i == j ? 1 : 0; continue; }
                double value = matrix[address];
                for (int k = Math.Max(0, i - bandwidth); k < j; k++)
                    value -= factor[i * stride + i - k] * factor[j * stride + j - k];
                factor[address] = i == j ? Math.Sqrt(value) : value / factor[j * stride];
            }
        }
        for (int i = 0; i < count; i++)
        {
            double value = solution[i];
            for (int j = Math.Max(0, i - bandwidth); j < i; j++) value -= factor[i * stride + i - j] * solution[j];
            solution[i] = value / factor[i * stride];
        }
        for (int i = count - 1; i >= 0; i--)
        {
            double value = solution[i];
            for (int j = i + 1; j < Math.Min(count, i + bandwidth + 1); j++) value -= factor[j * stride + j - i] * solution[j];
            solution[i] = value / factor[i * stride];
        }
    }
}
