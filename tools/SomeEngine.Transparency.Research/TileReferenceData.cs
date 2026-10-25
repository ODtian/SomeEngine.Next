using SomeEngine.Job;

namespace SomeEngine.Transparency.Research;

internal readonly record struct TileCoverage(int Left, int Top, int Right, int Bottom)
{
    internal int Count => (Right - Left + 1) * (Bottom - Top + 1);
}

internal sealed class TileReferenceData
{
    internal TileReferenceData(TileCoverage[] coverage, int columns, int rows)
    {
        Scheduler jobs = JobSystem.Current;
        int batches = Math.Min(coverage.Length, Math.Max(1, jobs.WorkerCount) * 4);
        int tileCount = columns * rows;
        var offsets = new int[batches * tileCount];
        jobs.ExecuteParallel(new CountTileReferencesJob(coverage, columns, tileCount, batches, offsets), batches, 1,
            [JobResourceAccess.Read(jobs, coverage), JobResourceAccess.Write(jobs, offsets)]);
        Ranges = new TileRange[tileCount];
        int total = 0;
        for (int tile = 0; tile < tileCount; tile++)
        {
            int first = total;
            for (int batch = 0; batch < batches; batch++)
            {
                int index = batch * tileCount + tile;
                int count = offsets[index];
                offsets[index] = total;
                total += count;
            }
            Ranges[tile] = new((uint)first, (uint)(total - first));
        }
        References = new uint[total];
        jobs.ExecuteParallel(new FillTileReferencesJob(coverage, columns, tileCount, batches, offsets, References), batches, 1,
            [JobResourceAccess.Read(jobs, coverage), JobResourceAccess.Write(jobs, offsets), JobResourceAccess.Write(jobs, References)]);
    }

    internal uint[] References { get; }
    internal TileRange[] Ranges { get; }

    private readonly struct CountTileReferencesJob(TileCoverage[] coverage, int columns,
        int tileCount, int batches, int[] counts) : IJobParallelFor
    {
        public void Execute(int batch)
        {
            int first = coverage.Length * batch / batches, last = coverage.Length * (batch + 1) / batches;
            int start = batch * tileCount;
            for (int index = first; index < last; index++)
            {
                TileCoverage area = coverage[index];
                for (int row = area.Top; row <= area.Bottom; row++)
                for (int column = area.Left; column <= area.Right; column++) counts[start + row * columns + column]++;
            }
        }
    }

    private readonly struct FillTileReferencesJob(TileCoverage[] coverage, int columns,
        int tileCount, int batches, int[] offsets, uint[] references) : IJobParallelFor
    {
        public void Execute(int batch)
        {
            int first = coverage.Length * batch / batches, last = coverage.Length * (batch + 1) / batches;
            int start = batch * tileCount;
            for (int index = first; index < last; index++)
            {
                TileCoverage area = coverage[index];
                for (int row = area.Top; row <= area.Bottom; row++)
                for (int column = area.Left; column <= area.Right; column++)
                    references[offsets[start + row * columns + column]++] = (uint)index;
            }
        }
    }
}
