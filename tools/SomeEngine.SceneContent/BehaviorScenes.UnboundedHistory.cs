using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.SceneContent;

public static partial class BehaviorScenes
{
    public static async Task CreateUnboundedHistoryDefaultAsync(string cookedDefault, string destination, string engineRoot)
    {
        string engineShaders = Path.Combine(engineRoot,"assets","Shaders");
        var project = new AssetProject(destination,[],shaderIncludeDirectories:[engineShaders],
            shaderTargets:[SlangCompileTarget.Dxil,SlangCompileTarget.Spirv]);
        EngineShaderContent.SetContentDirectories(project,[cookedDefault,engineRoot]);
        AssetManifest shared = AssetManifest.Load(Path.Combine(cookedDefault,"Library","AssetManifest"));
        await using var files = EngineShaderContent.OpenFiles(cookedDefault,shared);
        AssetEntry applicationEntry = shared.List(ApplicationSettingsAsset.FileType).Single();
        using var application = await ApplicationSettingsAsset.OpenAsync(files,new(applicationEntry.AssetGuid));
        ApplicationSettingsBlock settings = await application.GetBlockAsync<ApplicationSettingsBlock>();
        using var scene = await WorldAsset.OpenAsync(files,settings.Scene);
        using var world = await scene.LoadAsync(SceneSerialization.CreateRegistry());

        string shaderDirectory = Path.Combine(destination,"assets","Shaders");
        Directory.CreateDirectory(shaderDirectory);
        string sourcePath = Path.Combine(shaderDirectory,"unbounded_phong_history.slang");
        await File.WriteAllTextAsync(sourcePath,UnboundedHistoryMaterial);
        using var definition = new ShaderAsset { Name = "Static unbounded Phong history" };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = [Path.GetFileName(sourcePath)];
        string definitionPath = Path.ChangeExtension(sourcePath,".shader");
        await definition.SaveSourceAsync(definitionPath);
        await project.BuildAsync(definitionPath);

        using var template = await StandardMaterialAsset.OpenAsync(files,MaterialTemplates.Standard);
        using var material = template.CreateCopy();
        material.Name = "Static unbounded Phong history";
        MaterialBlock block = material.GetBlock<MaterialBlock>();
        block.Parent = template.Reference;
        block.Shader = definition.Reference;
        _ = new StandardMaterialParameters(block)
        {
            BaseColorTint = new(.8f,.8f,.8f,1), MetallicFactor = 0, Roughness = .8f,
        };
        project.RegisterAsset(await material.SaveAsync(Path.Combine(destination,"assets","Materials","unbounded-history.material.asset")));
        var (vertices,indices,attributes) = PrimitiveMeshGenerator.CreateIcoSphere(0);
        using MeshAsset mesh = ClusterBuilder.ProcessRaw(vertices,attributes,indices,[new(material.Id)],"Static unbounded Phong history");
        project.RegisterAsset(await mesh.SaveAsync(Path.Combine(destination,"assets","Meshes","unbounded-history.mesh.asset")));
        var staticObject = world.CreateEntity(new WorldTransform
        {
            Qvvs = new(new Vector3(40,0,30),Quaternion.Identity),
        });
        world.Add(staticObject,new MeshSource { Mesh = mesh.Reference, DepthCacheClass = DepthCacheClass.Static });
        Vector3 lightPosition = new(40,4,20);
        world.CreateEntity(new SpotLight(lightPosition,16,Vector3.Normalize(new Vector3(40,0,30)-lightPosition),
            MathF.Cos(MathF.PI/8),MathF.Cos(MathF.PI/6),Vector3.One,30));
        var cameras = world.Query(new QueryDefinitionBuilder().ReadWrite<SceneCamera>());
        world.ExecuteQuery(cameras,rows =>
        {
            foreach (var row in rows.Rows)
            {
                ref SceneCamera camera = ref row.ReadWrite<SceneCamera>();
                camera.Position = new(40,0,20);
                camera.Target = new(40,0,30);
                camera.VerticalFieldOfView = MathF.PI/3;
            }
        });
        world.ReleaseQuery(cameras);
        scene.WriteWorld(world,SceneSerialization.CreateRegistry());
        project.RegisterAsset(await scene.SaveAsync(Path.Combine(destination,shared.Assets[scene.Id].Path)));
        settings.Width = 128;
        settings.Height = 128;
        project.RegisterAsset(await application.SaveAsync(Path.Combine(destination,applicationEntry.Path)));
    }

    private const string UnboundedHistoryMaterial = """
        import material_interfaces;
        import triangle_surface;
        import phong_tessellation;
        import standard_material;
        [VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
        [ConservativeBounds("unbounded")]
        struct UnboundedPhongHistoryMaterial : IMaterial
        {
            typedef StaticVertexEval Vertex;
            typedef PhongTessellation Domain;
            typedef OpaqueVisibilityEvaluate<Vertex.Vertex> Visibility;
            StandardMaterial Surface;
            void evaluateSurface<TOutput:ISurfaceOutput>(SurfacePixelInput input,
                Domain.Surface surface,float3 bary,Domain.TriangleData triangle,TOutput output)
            { Surface.evaluateSurface<TOutput>(input,surface,bary,triangle,output); }
            void evaluateDebug<TOutput:ISurfaceOutput>(uint mode,SurfacePixelInput input,
                Domain.Surface surface,float3 bary,Domain.TriangleData triangle,TOutput output)
            { Surface.evaluateDebug<TOutput>(mode,input,surface,bary,triangle,output); }
        };
        """;
}
