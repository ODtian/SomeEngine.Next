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

/// <summary>GPU height editing, surface extraction, user attributes and shared Cluster geometry.</summary>
public sealed class GpuHeightTerrainExample : IDisposable
{
    private readonly Shader _drawShader;
    private readonly Shader _brushShader;

    private GpuHeightTerrainExample(Scheduler scheduler, Shader surface, Shader attributes, Shader build, Shader draw, Shader brush)
    {
        // The grid, chunk size, placements and UV mapping are example choices.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        Heights = terrain.CreateHeightField("Shared GPU ground", new HeightGrid(5, 5, 4));
        terrain.Surface = new HeightSurface(Heights);
        var adapter = new TerrainClusterAdapter(new MarchingCubesTerrainMesherGpu(surface),
            new(attributes, "CreateGroundAttributes", 20),
            new(build, "BuildGroundCluster", "example.terrain.position_normal_uv.v1", 64 * 32));
        Scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1));
        _drawShader = draw;
        _brushShader = brush;
    }

    public HeightField Heights { get; }
    public TerrainScene Scene { get; }

#if SOMEENGINE_DEVELOPMENT
    public static async Task<GpuHeightTerrainExample> CreateAsync(Scheduler scheduler, string contentDirectory)
    {
        string shaders = Path.Combine(contentDirectory, "assets", "Shaders");
        async Task<Shader> Load(string source, string directory)
        {
            var description = SlangShaderImporter.CreateSessionDescription(Path.Combine(directory, source),
                SlangShaderImporter.CreateDefaultTargets(SlangShaderImporter.GlobalSession), [ProjectPaths.EngineShaders, shaders]);
            using var block = new ShaderSourceBlock { SourceNames = [source] };
            return await block.CreateAsync(SlangShaderImporter.GlobalSession, description, source);
        }
        return new(scheduler, await Load("terrain_height_surface.slang", ProjectPaths.EngineShaders),
            await Load("gpu_terrain_attributes.slang", shaders), await Load("gpu_terrain_build.slang", shaders),
            await Load("gpu_terrain_material.slang", shaders), await Load("terrain_height_delta.slang", ProjectPaths.EngineShaders));
    }
#endif

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));
    public TerrainEdit AddHeight(float delta) => Heights.EditGpu(new HeightDeltaBrushGpu(_brushShader, Heights.Grid.Range, delta));

    public (int First, int Second) AddPlacements(Material first, Material second)
    {
        var chunks = new TerrainChunkRange(0, -1, 0, 1, 2, 1);
        int a = Scene.Add(new TerrainPlacement(Heights.Terrain, chunks, [CreateMaterial((UnlitMaterial)first)])
        {
            Transform = new TransformQvvs(new Vector3(-5, -2, 6), Quaternion.Identity),
        });
        int b = Scene.Add(new TerrainPlacement(Heights.Terrain, chunks, [CreateMaterial((UnlitMaterial)second)])
        {
            Transform = new TransformQvvs(new Vector3(1, -2, 6), Quaternion.Identity),
        });
        return (a, b);
    }

    private Material CreateMaterial(UnlitMaterial source)
    {
        var result = new UnlitMaterial
        {
            Shader = _drawShader, BaseColorTint = source.BaseColorTint,
            AlbedoMap = source.AlbedoMap, MaterialSampler = source.MaterialSampler,
        };
        return result;
    }

    public void Dispose()
    {
        Heights.Terrain.Dispose();
    }
}
