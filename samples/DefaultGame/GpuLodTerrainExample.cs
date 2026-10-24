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

/// <summary>The camera LOD and local detail example generated through GPU surface and transition cells.</summary>
public sealed class GpuLodTerrainExample : IDisposable
{
    private readonly Shader _drawShader;
    private readonly Shader _voxelShader;

    private GpuLodTerrainExample(Scheduler scheduler, Shader surface, Shader attributes, Shader build,
        Shader draw, Shader voxel)
    {
        // Area, lattice, blend border and refinement distances match the CPU example's choices.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        Heights = terrain.CreateHeightField("Rolling ground", new HeightGrid(33, 17, 8));
        Heights.EditCpu(new GroundHeights(Heights.Grid.Range));
        Voxels = terrain.CreateVoxelField("Editable detail", new VoxelGrid(8), 16f);
        var height = new HeightSurface(Heights);
        height.WriteVoxelsCpu(Voxels, new(0, -2, 0, 33, 13, 17));
        terrain.Surface = new BlendSurface(height, new VoxelSurface(Voxels), .5f,
            new TerrainBounds(new(0, -2, 0), new(32, 10, 16)), 2f);
        var adapter = new TerrainClusterAdapter(new MarchingCubesTerrainMesherGpu(surface, "ExtractBlendSurface"),
            new(attributes, "CreateGroundAttributes", 20),
            new(build, "BuildGroundCluster", "example.terrain.position_normal_uv.v1", 64 * 32));
        Scene = new TerrainScene(adapter, new(4, 1f));
        _drawShader = draw;
        _voxelShader = voxel;
    }

    public HeightField Heights { get; }
    public VoxelField Voxels { get; }
    public TerrainScene Scene { get; }

#if SOMEENGINE_DEVELOPMENT
    public static async Task<GpuLodTerrainExample> CreateAsync(Scheduler scheduler, string contentDirectory)
    {
        string shaders = Path.Combine(contentDirectory, "assets", "Shaders");
        async Task<Shader> Load(string source, string directory)
        {
            var description = SlangShaderImporter.CreateSessionDescription(Path.Combine(directory, source),
                SlangShaderImporter.CreateDefaultTargets(SlangShaderImporter.GlobalSession), [ProjectPaths.EngineShaders, shaders]);
            using var block = new ShaderSourceBlock { SourceNames = [source] };
            return await block.CreateAsync(SlangShaderImporter.GlobalSession, description, source);
        }
        return new(scheduler, await Load("terrain_blend_surface.slang", ProjectPaths.EngineShaders),
            await Load("gpu_terrain_attributes.slang", shaders), await Load("gpu_terrain_build.slang", shaders),
            await Load("gpu_terrain_material.slang", shaders), await Load("terrain_voxel_delta.slang", ProjectPaths.EngineShaders));
    }
#endif

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));

    public TerrainPlacement CreatePlacement(Material material)
    {
        var source = (UnlitMaterial)material;
        var drawMaterial = new UnlitMaterial
        {
            Shader = _drawShader, BaseColorTint = source.BaseColorTint,
            AlbedoMap = source.AlbedoMap, MaterialSampler = source.MaterialSampler,
        };
        return new TerrainPlacement(Heights.Terrain, new(0, 0, 0, 2, 1, 1), [drawMaterial], new TerrainLodDistances(5, 9))
        {
            Transform = new TransformQvvs(new(-16, -4, 10), Quaternion.Identity),
        };
    }

    /// <summary>The sphere position, radius and scalar delta match the CPU example's brush values.</summary>
    public TerrainEdit RaiseDetail() =>
        Voxels.EditGpu(new VoxelSphereDeltaBrushGpu(_voxelShader, Voxels.Grid, new(16, 3, 8), 4f, -2f));

    private readonly record struct GroundHeights(HeightRegion Range) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z) => 3f + .6f * MathF.Sin(x * .35f) * MathF.Cos(z * .3f);
    }

    public void Dispose() => Heights.Terrain.Dispose();
}
