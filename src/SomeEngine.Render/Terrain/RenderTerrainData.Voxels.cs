using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

public sealed partial class RenderTerrainData
{
    internal VoxelGpuInput CreateVoxelInput(ref RenderGraphFrame frame, ref RenderGraphFrameExtension extension,
        VoxelGrid grid, VoxelFieldData source, VoxelRegion range)
    {
        int size = grid.PageSize;
        int minX = grid.PageCoordinate(range.MinX);
        int minY = grid.PageCoordinate(range.MinY);
        int minZ = grid.PageCoordinate(range.MinZ);
        int width = grid.PageCoordinate(range.MaxX - 1) - minX + 1;
        int height = grid.PageCoordinate(range.MaxY - 1) - minY + 1;
        int depth = grid.PageCoordinate(range.MaxZ - 1) - minZ + 1;
        var offsets = new int[width * height * depth];
        Array.Fill(offsets, -1);
        var pages = new List<TerrainFieldPage>();
        for (int z = 0; z < depth; z++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (source.Pages.TryGetValue((minX + x, minY + y, minZ + z), out TerrainFieldPage? page))
            {
                offsets[(z * height + y) * width + x] = pages.Count * size * size * size;
                pages.Add(page);
            }

        GraphBufferSlice samples;
        if (pages.Count == 0)
            samples = frame.Upload<float>([0f], BufferUsages.ShaderRead);
        else
        {
            ulong bytesPerPage = (ulong)size * (ulong)size * (ulong)size * sizeof(float);
            ulong bytes = (ulong)pages.Count * bytesPerPage;
            GraphBufferId packed = frame.CreateBuffer(new BufferDesc(bytes,
                BufferUsages.CopyDestination | BufferUsages.ShaderRead, "Terrain voxel brush input"));
            for (int index = 0; index < pages.Count; index++)
            {
                GraphBufferId page = GetInput(ref frame, ref extension, pages[index]);
                Copy(ref extension, "Gather terrain voxel input", new(page, BufferRange.Whole), packed,
                    (ulong)index * bytesPerPage, bytesPerPage);
            }
            samples = new(packed, new(0, bytes));
        }
        return new(samples, frame.Upload<int>(offsets, BufferUsages.ShaderRead, "Terrain voxel source page table"),
            minX, minY, minZ, width, height);
    }
}
