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

/// <summary>A height and voxel surface graph whose field edits and geometry run on the GPU.</summary>
public sealed class GpuBlendTerrainExample : IDisposable
{
    private readonly Shader _drawShader;
    private readonly Shader _heightShader;
    private readonly Shader _voxelShader;

    private GpuBlendTerrainExample(Scheduler scheduler, Shader surface, Shader attributes, Shader build,
        Shader draw, Shader height, Shader voxel)
    {
        // Eight metres, one metre sampling and a two metre blend border are example choices.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        Heights = terrain.CreateHeightField("Ground", new HeightGrid(9, 9, 4));
        Voxels = terrain.CreateVoxelField("Local detail", new VoxelGrid(4));
        var heightSurface = new HeightSurface(Heights);
        heightSurface.WriteVoxelsCpu(Voxels, SampleRegion);
        terrain.Surface = new BlendSurface(heightSurface, new VoxelSurface(Voxels), 1f,
            new TerrainBounds(new(0, -8, 0), new(8, 8, 8)), 2f);
        var adapter = new TerrainClusterAdapter(new MarchingCubesTerrainMesherGpu(surface, "ExtractBlendSurface"),
            new(attributes, "CreateGroundAttributes", 20),
            new(build, "BuildGroundCluster", "example.terrain.position_normal_uv.v1", 64 * 32));
        Scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1));
        _drawShader = draw;
        _heightShader = height;
        _voxelShader = voxel;
    }

    public HeightField Heights { get; }
    public VoxelField Voxels { get; }
    public VoxelRegion SampleRegion { get; } = new(0, -8, 0, 9, 17, 9);
    public TerrainScene Scene { get; }

#if SOMEENGINE_DEVELOPMENT
    public static async Task<GpuBlendTerrainExample> CreateAsync(Scheduler scheduler, string contentDirectory)
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
            await Load("gpu_terrain_material.slang", shaders), await Load("terrain_height_delta.slang", ProjectPaths.EngineShaders),
            await Load("terrain_voxel_delta.slang", ProjectPaths.EngineShaders));
    }
#endif

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));
    public TerrainEdit AddHeight(float delta) =>
        Heights.EditGpu(new HeightDeltaBrushGpu(_heightShader, Heights.Grid.Range, delta));
    public TerrainEdit AddSphereDelta(Vector3 center, float radius, float delta) =>
        Voxels.EditGpu(new VoxelSphereDeltaBrushGpu(_voxelShader, Voxels.Grid, center, radius, delta));

    public int AddPlacement(Material material)
    {
        var source = (UnlitMaterial)material;
        var drawMaterial = new UnlitMaterial
        {
            Shader = _drawShader, BaseColorTint = source.BaseColorTint,
            AlbedoMap = source.AlbedoMap, MaterialSampler = source.MaterialSampler,
        };
        return Scene.Add(new TerrainPlacement(Voxels.Terrain, new(0, -1, 0, 2, 2, 2), [drawMaterial])
        {
            Transform = new TransformQvvs(new Vector3(-4, -2, 6), Quaternion.Identity),
        });
    }

    public void Dispose() => Heights.Terrain.Dispose();
}
