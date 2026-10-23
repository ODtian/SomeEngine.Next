using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>Runs field work on a borrowed scheduler. The caller controls the scheduler's lifetime.</summary>
public sealed partial class TerrainEditing(Scheduler scheduler)
{
    public Scheduler Scheduler { get; } = scheduler;

    internal TerrainEdit EditCpu<TBrush>(HeightField field, TBrush brush, JobHandle dependency)
        where TBrush : struct, IHeightBrushCpu
    {
        lock (field.Terrain.Gate)
        {
            HeightFieldData source = field.Data;
            HeightRegion range = brush.Range.Intersect(field.Grid.Range);
            int size = field.Grid.PageSize;
            var pages = new Dictionary<(int X, int Z), TerrainFieldPage>(source.Pages);
            var jobs = new List<JobHandle>();
            HeightFieldCpuRead inputData = field.CaptureCpuRead(range.Expand(brush.ReadBorder));
            JobHandle input = Scheduler.CombineDependencies([inputData.Completion, dependency]);
            ulong revision = field.Terrain.NextRevision();

            for (int pageZ = range.MinZ / size; pageZ * size < range.MaxZ; pageZ++)
            for (int pageX = range.MinX / size; pageX * size < range.MaxX; pageX++)
            {
                var pageRange = new HeightRegion(pageX * size, pageZ * size, size, size);
                HeightRegion writeRange = pageRange.Intersect(range);
                HeightRegion readRange = writeRange.Expand(brush.ReadBorder).Intersect(field.Grid.Range);
                var read = new HeightFieldRead(field.Grid, field.InitialHeight, source, readRange);
                var destination = new float[size * size];
                source.Pages.TryGetValue((pageX, pageZ), out TerrainFieldPage? previous);

                var accesses = new List<JobResourceAccess> { JobResourceAccess.Write(Scheduler, destination) };
                for (int readZ = readRange.MinZ / size; readZ * size < readRange.MaxZ; readZ++)
                for (int readX = readRange.MinX / size; readX * size < readRange.MaxX; readX++)
                    if (source.Pages.TryGetValue((readX, readZ), out TerrainFieldPage? page))
                        accesses.Add(JobResourceAccess.Read(Scheduler, page.Samples!));

                JobHandle job = Scheduler.Schedule(
                    new HeightPageJob<TBrush>(brush, read, writeRange, previous?.Samples, destination, field.InitialHeight),
                    CollectionsMarshal.AsSpan(accesses), input);
                jobs.Add(job);
                pages[(pageX, pageZ)] = new TerrainFieldPage(destination, job, revision);
            }

            var edit = new TerrainEdit(revision, Scheduler.CombineDependencies(CollectionsMarshal.AsSpan(jobs)));
            field.Data = new HeightFieldData(pages, edit);
            return edit;
        }
    }

    private readonly struct HeightPageJob<TBrush>(TBrush brush, HeightFieldRead source, HeightRegion range,
        float[]? previous, float[] destination, float initialHeight) : IJob
        where TBrush : struct, IHeightBrushCpu
    {
        public void Execute()
        {
            if (previous is null)
                Array.Fill(destination, initialHeight);
            else
                previous.CopyTo(destination, 0);

            int size = source.Grid.PageSize;
            for (int z = range.MinZ; z < range.MaxZ; z++)
            for (int x = range.MinX; x < range.MaxX; x++)
                destination[(z % size) * size + x % size] = brush.Apply(source, x, z);
        }
    }
}
