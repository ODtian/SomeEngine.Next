using SomeEngine.ECS;
using SomeEngine.Job;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>Device geometry completion and the later rendering-world batch binding.</summary>
public readonly record struct TerrainGpuBuild(ulong Revision, JobHandle Completion, JobHandle Visibility);

/// <summary>The user's attribute kernel, fixed field inputs and optional triangle-material kernel.</summary>
public sealed record TerrainGpuAttributes(Shader Shader, string EntryPoint, uint VertexBytes,
    int ReadBorder = 0, byte MaterialIndex = 0)
{
    public TerrainFieldInputs Fields { get; init; }
    public string? MaterialEntryPoint { get; init; }
    public uint MaterialCount { get; init; } = 1;
}

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
            TerrainBounds bounds = chunks[0].Bounds.Volume.Expand(ReadBorder * chunks[0].Bounds.CellSize);
            foreach (TerrainGpuChunkBuild chunk in chunks.AsSpan(1))
                bounds = bounds.Union(chunk.Bounds.Volume.Expand(ReadBorder * chunk.Bounds.CellSize));
            TerrainGpuSurfaceData input = TerrainGpuSurfaceData.Capture(terrain.Surface!, bounds);
            var completion = new TerrainCompletion(terrain.Editing.Scheduler);
            var visibility = new TerrainCompletion(terrain.Editing.Scheduler);
            var request = new TerrainGeometryRequest(terrain.NextRequestSequence(), input.Completion,
                completion, visibility, scene, terrain.Revision, input, chunks);
            terrain.RetainRequest(request);
            return request;
        }
    }
}

internal sealed class TerrainGeometryRequest(long sequence, JobHandle dependency, TerrainCompletion completion,
    TerrainCompletion visibility, object scene, ulong revision, TerrainGpuSurfaceData surface, TerrainGpuChunkBuild[] chunks)
    : TerrainDataRequest(sequence, dependency, completion)
{
    internal object Scene { get; } = scene;
    internal ulong Revision { get; } = revision;
    internal TerrainGpuSurfaceData? Surface { get; private set; } = surface;
    internal TerrainGpuChunkBuild[] Chunks { get; } = chunks;
    internal TerrainCompletion Visibility { get; } = visibility;
    internal void ReleaseInput() => Surface = null;
}

internal readonly record struct TerrainGpuChunkBuild(int GeometryId, TerrainChunkBounds Bounds);
internal readonly record struct TerrainGpuSource(object Scene) : IComponent;
