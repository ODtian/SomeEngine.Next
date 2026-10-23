using SomeEngine.ECS;
using SomeEngine.Job;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>Device geometry completion and the later rendering-world batch binding.</summary>
public readonly record struct TerrainGpuBuild(ulong Revision, JobHandle Completion, JobHandle Visibility);

/// <summary>The user's attribute kernel and output size. MaterialIndex selects this build's material group.</summary>
public sealed record TerrainGpuAttributes(Shader Shader, string EntryPoint, uint VertexBytes,
    int ReadBorder = 0, byte MaterialIndex = 0);

/// <summary>Connects GPU surface extraction and user attributes to the common GPU Cluster builder.</summary>
public sealed class TerrainClusterAdapter(ITerrainMesherGpu mesher, TerrainGpuAttributes attributes,
    ClusterGpuVertexEncoder encoder)
{
    public ITerrainMesherGpu Mesher { get; } = mesher;
    public TerrainGpuAttributes Attributes { get; } = attributes;
    public ClusterGpuVertexEncoder Encoder { get; } = encoder;
    public int ReadBorder => Mesher.ReadBorder + Attributes.ReadBorder;

    internal TerrainGeometryRequest Capture(object scene, Resources.Terrain terrain, TerrainGpuChunkBuild[] chunks)
    {
        lock (terrain.Gate)
        {
            var height = (HeightSurface)terrain.Surface!;
            HeightField field = height.Field;
            var completion = new TerrainCompletion(terrain.Editing.Scheduler);
            var visibility = new TerrainCompletion(terrain.Editing.Scheduler);
            TerrainBounds bounds = chunks[0].Bounds.Volume.Expand(ReadBorder * chunks[0].Bounds.CellSize);
            foreach (TerrainGpuChunkBuild chunk in chunks.AsSpan(1))
                bounds = bounds.Union(chunk.Bounds.Volume.Expand(ReadBorder * chunk.Bounds.CellSize));
            float spacing = field.Grid.SampleSpacing;
            int minX = Math.Clamp((int)MathF.Floor(bounds.Min.X / spacing), 0, field.Grid.Width - 1);
            int minZ = Math.Clamp((int)MathF.Floor(bounds.Min.Z / spacing), 0, field.Grid.Depth - 1);
            int maxX = Math.Clamp((int)MathF.Floor(bounds.Max.X / spacing) + 2, 1, field.Grid.Width);
            int maxZ = Math.Clamp((int)MathF.Floor(bounds.Max.Z / spacing) + 2, 1, field.Grid.Depth);
            var input = new TerrainHeightGpuData(field.Grid, field.InitialHeight, field.Data,
                new(minX, minZ, maxX - minX, maxZ - minZ));
            var request = new TerrainGeometryRequest(terrain.NextRequestSequence(), field.Data.Edit.Completion,
                completion, visibility, scene, terrain.Revision, input, chunks);
            terrain.RetainRequest(request);
            return request;
        }
    }
}

internal sealed class TerrainGeometryRequest(long sequence, JobHandle dependency, TerrainCompletion completion,
    TerrainCompletion visibility, object scene, ulong revision, TerrainHeightGpuData height, TerrainGpuChunkBuild[] chunks)
    : TerrainDataRequest(sequence, dependency, completion)
{
    internal object Scene { get; } = scene;
    internal ulong Revision { get; } = revision;
    internal TerrainHeightGpuData Height { get; } = height;
    internal TerrainGpuChunkBuild[] Chunks { get; } = chunks;
    internal TerrainCompletion Visibility { get; } = visibility;
}

internal readonly record struct TerrainHeightGpuData(HeightGrid Grid, float InitialHeight,
    HeightFieldData Data, HeightRegion Range);
internal readonly record struct TerrainGpuChunkBuild(int GeometryId, TerrainChunkBounds Bounds);
internal readonly record struct TerrainGpuSource(object Scene) : IComponent;
