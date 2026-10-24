using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
#if SOMEENGINE_DEVELOPMENT
using SomeEngine.Assets.Importers;
#endif
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Render.Terrain;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using SomeEngine.Terrains;

namespace DefaultGame;

/// <summary>Example material fields, explicit vertex bytes and a quadratic displacement surface.</summary>
public sealed class TerrainMaterialExample : IDisposable
{
    private const string VertexEncoding = "example.terrain.position_uv_lift.v1";
    private readonly Shader _heightShader;

    private TerrainMaterialExample(TerrainScene scene, HeightField lift, VoxelField regions, Shader draw, Shader height)
    {
        Scene = scene;
        Lift = lift;
        Regions = regions;
        _heightShader = height;
        Materials =
        [
            new() { Shader = draw, BaseColorTint = new(1, 0, 0, 1) },
            new() { Shader = draw, BaseColorTint = new(0, 1, 0, 1) },
        ];
    }

    public HeightField Lift { get; }
    public VoxelField Regions { get; }
    public TerrainScene Scene { get; }
    public TerrainDisplacementMaterial[] Materials { get; }

#if SOMEENGINE_DEVELOPMENT
    public static async Task<TerrainMaterialExample> CreateCpuAsync(Scheduler scheduler, string contentDirectory)
    {
        string shaders = Path.Combine(contentDirectory, "assets", "Shaders");
        var data = CreateFields(scheduler);
        var adapter = new TerrainClusterAdapter<GroundAttributes>(new AttributeGenerator(data.Lift.Id, data.Regions.Id),
            new VertexEncoder(), ClusterBuildStrategy.Realtime);
        return new(new TerrainScene(adapter, new(4, 1f)), data.Lift, data.Regions,
            await LoadShaderAsync(shaders, "terrain_displacement_material.slang"),
            await LoadShaderAsync(ProjectPaths.EngineShaders, "terrain_height_delta.slang"));
    }

    public static async Task<TerrainMaterialExample> CreateGpuAsync(Scheduler scheduler, string contentDirectory)
    {
        string shaders = Path.Combine(contentDirectory, "assets", "Shaders");
        var data = CreateFields(scheduler);
        var attributes = new TerrainGpuAttributes(await LoadShaderAsync(shaders, "terrain_material_attributes.slang"),
            "CreateGroundAttributes", 12)
        {
            Fields = new([data.Lift.Id], [data.Regions.Id]),
            MaterialEntryPoint = "CreateGroundMaterials",
            MaterialCount = 2,
        };
        var adapter = new TerrainClusterAdapter(
            new MarchingCubesTerrainMesherGpu(await LoadShaderAsync(ProjectPaths.EngineShaders, "terrain_height_surface.slang")),
            attributes, new(await LoadShaderAsync(shaders, "terrain_material_build.slang"), "BuildGroundCluster",
                VertexEncoding, 64 * 24));
        return new(new TerrainScene(adapter, new(4, 1f)), data.Lift, data.Regions,
            await LoadShaderAsync(shaders, "terrain_displacement_material.slang"),
            await LoadShaderAsync(ProjectPaths.EngineShaders, "terrain_height_delta.slang"));
    }

    private static async Task<Shader> LoadShaderAsync(string shaders, string source)
    {
        var description = SlangShaderImporter.CreateSessionDescription(Path.Combine(shaders, source),
            SlangShaderImporter.CreateDefaultTargets(SlangShaderImporter.GlobalSession), [ProjectPaths.EngineShaders, shaders]);
        using var block = new ShaderSourceBlock { SourceNames = [source] };
        return await block.CreateAsync(SlangShaderImporter.GlobalSession, description, source);
    }
#endif

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));

    public int AddPlacement() => Scene.Add(new TerrainPlacement(Lift.Terrain, new(0, -1, 0, 2, 2, 1), Materials)
    {
        Transform = new TransformQvvs(new(-4, -1, 6), Quaternion.Identity),
    });

    public TerrainEdit RaiseLiftGpu(float delta) =>
        Lift.EditGpu(new HeightDeltaBrushGpu(_heightShader, Lift.Grid.Range, delta));

    /// <summary>Persists the example's named field inputs beside the supplied material asset references.</summary>
    public async ValueTask SaveAsync(string path, IReadOnlyList<AssetId<MaterialAsset>> materials,
        CancellationToken cancellationToken = default)
    {
        using var bindings = new TerrainMaterialBlock
        {
            Materials = materials.ToArray(),
            HeightSources = [new(0, "Lift", Lift.Id), new(1, "Lift", Lift.Id)],
            VoxelSources = [new(0, "Region", Regions.Id), new(1, "Region", Regions.Id)],
        };
        using var asset = new TerrainAsset { Name = "Displaced material fields" };
        await asset.UpdateAsync(await Lift.Terrain.CaptureCpuAsync().ConfigureAwait(false),
            bindings, cancellationToken).ConfigureAwait(false);
        await asset.SaveAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static (HeightField Lift, VoxelField Regions) CreateFields(Scheduler scheduler)
    {
        // The eight-by-four metre plane, two regions and normalized lift ramp are example data.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField ground = terrain.CreateHeightField("Ground", new(9, 5, 4));
        terrain.Surface = new HeightSurface(ground);
        HeightField lift = terrain.CreateHeightField("Lift", new(9, 5, 4));
        lift.EditCpu(new LiftRamp(lift.Grid.Range));
        VoxelField regions = terrain.CreateVoxelField("Region", new(4));
        regions.EditCpu(new MaterialRegions(new(0, -2, 0, 9, 5, 5)));
        return (lift, regions);
    }

    private readonly record struct LiftRamp(HeightRegion Range) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z) => .1f + x * .1f;
    }

    private readonly record struct MaterialRegions(VoxelRegion Range) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => x - 4f;
    }

    private readonly record struct GroundAttributes(Vector2 TexCoord, float Lift);

    private sealed class AttributeGenerator(int lift, int regions) : ITerrainAttributeGenerator<GroundAttributes>
    {
        public int ReadBorder => 0;
        public TerrainFieldInputs Fields { get; } = new([lift], [regions]);
        public GroundAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point)
        {
            var uv = new Vector2(point.SamplePosition.X, point.SamplePosition.Z);
            return new(uv, surface.GetHeightField(lift).Sample(uv));
        }
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) =>
            surface.GetVoxelField(regions).Sample(position) < 0f ? (byte)0 : (byte)1;
    }

    private sealed class VertexEncoder : IClusterVertexEncoder<GroundAttributes>
    {
        public string Compatibility => VertexEncoding;
        public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions, ReadOnlySpan<GroundAttributes> attributes,
            ReadOnlySpan<uint> clusterVertices)
        {
            byte[] blob = new byte[clusterVertices.Length * 24];
            Span<float> values = MemoryMarshal.Cast<byte, float>(blob.AsSpan());
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int vertex = 0; vertex < clusterVertices.Length; vertex++)
            {
                int source = (int)clusterVertices[vertex];
                Vector3 position = positions[source];
                GroundAttributes attribute = attributes[source];
                int offset = vertex * 6;
                values[offset] = position.X; values[offset + 1] = position.Y; values[offset + 2] = position.Z;
                values[offset + 3] = attribute.TexCoord.X; values[offset + 4] = attribute.TexCoord.Y;
                values[offset + 5] = attribute.Lift;
                min = Vector3.Min(min, position); max = Vector3.Max(max, position);
            }
            return new(blob, min, max);
        }
    }

    public void Dispose()
    {
        Lift.Terrain.Dispose();
    }
}
