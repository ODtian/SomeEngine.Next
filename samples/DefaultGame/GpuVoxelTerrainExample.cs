using System.Numerics;
#if SOMEENGINE_DEVELOPMENT
using SomeEngine.Assets.Importers;
#endif
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Job;
using SomeEngine.Render.Terrain;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using SomeEngine.Terrains;

namespace DefaultGame;

/// <summary>A sparse voxel field with GPU editing, surface extraction and native Cluster geometry.</summary>
public sealed class GpuVoxelTerrainExample : IDisposable
{
    private readonly Shader _drawShader;
    private readonly Shader _brushShader;
    private Material? _material;

    private GpuVoxelTerrainExample(Scheduler scheduler, Shader surface, Shader attributes, Shader build, Shader draw, Shader brush)
    {
        // One metre sampling, four-cell chunks and the sphere region are example choices.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        Field = terrain.CreateVoxelField("GPU signed sphere", new VoxelGrid(4));
        terrain.Surface = new VoxelSurface(Field);
        var adapter = new TerrainClusterAdapter(new MarchingCubesTerrainMesherGpu(surface, "ExtractVoxelSurface"),
            new(attributes, "CreateGroundAttributes", 20),
            new(build, "BuildGroundCluster", "example.terrain.position_normal_uv.v1", 64 * 32));
        Scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1));
        Field.EditCpu(new SphereSamples(SampleRegion, new(-1, 0, 0)));
        _drawShader = draw;
        _brushShader = brush;
    }

    public VoxelField Field { get; }
    public TerrainScene Scene { get; }
    public VoxelRegion SampleRegion { get; } = new(-3, -3, -3, 7, 7, 7);

#if SOMEENGINE_DEVELOPMENT
    public static async Task<GpuVoxelTerrainExample> CreateAsync(Scheduler scheduler, string contentDirectory)
    {
        string shaders = Path.Combine(contentDirectory, "assets", "Shaders");
        async Task<Shader> Load(string source, string directory)
        {
            var description = SlangShaderImporter.CreateSessionDescription(Path.Combine(directory, source),
                SlangShaderImporter.CreateDefaultTargets(SlangShaderImporter.GlobalSession), [ProjectPaths.EngineShaders, shaders]);
            using var block = new ShaderSourceBlock { SourceNames = [source] };
            return await block.CreateAsync(SlangShaderImporter.GlobalSession, description, source);
        }
        return new(scheduler, await Load("terrain_voxel_surface.slang", ProjectPaths.EngineShaders),
            await Load("gpu_terrain_attributes.slang", shaders), await Load("gpu_terrain_build.slang", shaders),
            await Load("gpu_terrain_material.slang", shaders), await Load("terrain_voxel_delta.slang", ProjectPaths.EngineShaders));
    }
#endif

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));
    public TerrainEdit AddSphereDelta(Vector3 center, float radius, float delta) =>
        Field.EditGpu(new VoxelSphereDeltaBrushGpu(_brushShader, Field.Grid, center, radius, delta));

    public int AddPlacement(Material material)
    {
        var source = (UnlitMaterial)material;
        _material = new UnlitMaterial
        {
            Shader = _drawShader, BaseColorTint = source.BaseColorTint,
            AlbedoMap = source.AlbedoMap, MaterialSampler = source.MaterialSampler,
        };
        return Scene.Add(new TerrainPlacement(Field.Terrain, new(-1, -1, -1, 2, 2, 2), [_material])
        {
            Transform = new TransformQvvs(new Vector3(0, 0, 6), Quaternion.Identity),
        });
    }

    public void Dispose() => _material?.Dispose();

    private readonly record struct SphereSamples(VoxelRegion Range, Vector3 Center) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) =>
            Vector3.Distance(source.Grid.SamplePosition(x, y, z), Center) - 1f;
    }
}
