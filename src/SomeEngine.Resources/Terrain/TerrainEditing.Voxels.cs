using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Resources;

public sealed partial class TerrainEditing
{
    internal TerrainEdit EditCpu<TBrush>(VoxelField field, TBrush brush, JobHandle dependency)
        where TBrush : struct, IVoxelBrushCpu
    {
        lock (field.Terrain.Gate)
        {
            VoxelFieldData source = field.Data;
            VoxelRegion range = brush.Range;
            VoxelGrid grid = field.Grid;
            int size = grid.PageSize;
            var pages = new Dictionary<(int X, int Y, int Z), TerrainFieldPage>(source.Pages);
            var jobs = new List<JobHandle>();
            VoxelFieldCpuRead inputData = field.CaptureCpuRead(range.Expand(brush.ReadBorder));
            JobHandle input = Scheduler.CombineDependencies([inputData.Completion, dependency]);

            for (int pageZ = grid.PageCoordinate(range.MinZ); pageZ * size < range.MaxZ; pageZ++)
            for (int pageY = grid.PageCoordinate(range.MinY); pageY * size < range.MaxY; pageY++)
            for (int pageX = grid.PageCoordinate(range.MinX); pageX * size < range.MaxX; pageX++)
            {
                var pageRange = new VoxelRegion(pageX * size, pageY * size, pageZ * size, size, size, size);
                VoxelRegion writeRange = pageRange.Intersect(range);
                VoxelRegion readRange = writeRange.Expand(brush.ReadBorder);
                var read = new VoxelFieldRead(grid, field.InitialValue, source, readRange);
                var destination = new float[size * size * size];
                source.Pages.TryGetValue((pageX, pageY, pageZ), out TerrainFieldPage? previous);

                var accesses = new List<JobResourceAccess> { JobResourceAccess.Write(Scheduler, destination) };
                for (int readZ = grid.PageCoordinate(readRange.MinZ); readZ * size < readRange.MaxZ; readZ++)
                for (int readY = grid.PageCoordinate(readRange.MinY); readY * size < readRange.MaxY; readY++)
                for (int readX = grid.PageCoordinate(readRange.MinX); readX * size < readRange.MaxX; readX++)
                    if (source.Pages.TryGetValue((readX, readY, readZ), out TerrainFieldPage? page))
                        accesses.Add(JobResourceAccess.Read(Scheduler, page.Samples!));

                JobHandle job = Scheduler.Schedule(
                    new VoxelPageJob<TBrush>(brush, read, writeRange, pageRange, previous?.Samples, destination, field.InitialValue),
                    CollectionsMarshal.AsSpan(accesses), input);
                jobs.Add(job);
                pages[(pageX, pageY, pageZ)] = new TerrainFieldPage(destination, job);
            }

            var edit = new TerrainEdit(field.Terrain.NextRevision(), Scheduler.CombineDependencies(CollectionsMarshal.AsSpan(jobs)));
            field.Data = new VoxelFieldData(pages, edit);
            return edit;
        }
    }

    private readonly struct VoxelPageJob<TBrush>(TBrush brush, VoxelFieldRead source, VoxelRegion range,
        VoxelRegion pageRange, float[]? previous, float[] destination, float initialValue) : IJob
        where TBrush : struct, IVoxelBrushCpu
    {
        public void Execute()
        {
            if (previous is null)
                Array.Fill(destination, initialValue);
            else
                previous.CopyTo(destination, 0);

            int size = source.Grid.PageSize;
            for (int z = range.MinZ; z < range.MaxZ; z++)
            for (int y = range.MinY; y < range.MaxY; y++)
            for (int x = range.MinX; x < range.MaxX; x++)
            {
                int index = ((z - pageRange.MinZ) * size + y - pageRange.MinY) * size + x - pageRange.MinX;
                destination[index] = brush.Apply(source, x, y, z);
            }
        }
    }
}
