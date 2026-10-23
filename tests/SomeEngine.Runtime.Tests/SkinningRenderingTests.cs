using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using Game.Build;
using Game.Importing;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Runtime.Tests;

public sealed class SkinningRenderingTests
{
    [Fact]
    public async Task UniformGpuClipMatchesInheritedCpuPosesAndVisibleGeometryThroughTheApplication()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        (Skeleton parentDefinition, Skeleton childDefinition) = GpuPoseExamples.Definitions();
        var storage = new PoseStorage();
        using SkeletonInstance parent = storage.CreateInstance(parentDefinition);
        using SkeletonInstance first = storage.CreateInstance(childDefinition, parent);
        using SkeletonInstance second = storage.CreateInstance(childDefinition, parent);
        AnimationClip parentClip = GpuPoseExamples.Clip(parentDefinition, 0);
        AnimationClip childClip = GpuPoseExamples.Clip(childDefinition, 1);
        QuantizedClipData parentData = QuantizedClipData.Encode(parentClip, 4), childData = QuantizedClipData.Encode(childClip, 4);
        Entity[] targets = new Entity[3];
        var repeat = new GpuPoseExamples.RepeatLastInput(targets);
        var reads = new List<GpuPoseReadback>();
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                AnimationSystems.Add(frame.Systems.Animation, repeat, AnimationPhase.Completion);
                targets[0] = GpuPoseAnimation.CreateTarget(frame.World, parent);
                targets[1] = GpuPoseAnimation.CreateTarget(frame.World, first);
                targets[2] = GpuPoseAnimation.CreateTarget(frame.World, second);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<GpuClipParameters, GpuClipLeaf>(targets[0], new(parentData));
                builder.Play();
                using var children = new AnimationBuilder(frame.World);
                children.Leaf<GpuClipParameters, GpuClipLeaf>(Entity.Null, new(childData));
                using CompiledGraph graph = children.Compile();
                graph.Play(frame.World, targets[1]);
                PropertyAnimation.SetSpeed(frame.World, graph.Play(frame.World, targets[2]), .5f);
                foreach (Entity mesh in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0;
                    frame.World.Add(mesh, new MeshSkin(right ? second : first, GpuBinding(right)));
                }
            },
            UpdateFrame = frame => repeat.Repeat = frame.Index == 5,
        };
        SceneImage? animated = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 6,
            [new(4, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); }),
             new(5, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); edit.CaptureImage(image => animated = image); })]),
            "--no-taa", "--no-vsm", "--fixed-step", "0.125");
        Assert.Equal(6, reads.Count);
        Matrix4x4[][] current = Reference(.625f), previous = Reference(.5f);
        for (int index = 0; index < reads.Count; index++)
        {
            GpuPoseReadback result = reads[index];
            int section = index % 3;
            Assert.Equal(1u, result.Starts);
            // The app evaluates the initial zero-delta input before its five timed updates.
            Assert.Equal(6ul, result.InputNumber);
            Assert.Equal(.625, result.CalculationTime);
            Assert.Equal(section == 2 ? .3125f : .625f, result.SampleTime);
            for (int bone = 0; bone < current[section].Length; bone++)
            {
                GpuPoseExamples.Near(current[section][bone], result.Current.Span[bone]);
                GpuPoseExamples.Near(previous[section][bone], result.Previous.Span[bone]);
            }
        }
        Matrix4x4[][] visibleCurrent = Reference(.625f, quantized: true), visiblePrevious = Reference(.5f, quantized: true);
        await content.WriteReferenceMeshesAsync((prior, right) =>
        {
            Matrix4x4[][] poses = prior ? visiblePrevious : visibleCurrent;
            SkinBinding binding = GpuBinding(right);
            return (binding.InverseBindMatrices[0] * poses[0][0], binding.InverseBindMatrices[1] * poses[right ? 2 : 1][1]);
        });
        await content.SetReferenceShaderAsync();
        SceneImage? reference = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.Project.RootPath, 6,
            [new(5, edit => edit.CaptureImage(image => reference = image))]), "--no-taa", "--no-vsm", "--fixed-step", "0.125");
        SceneImage expected = Assert.IsType<SceneImage>(reference);
        int visible = 0;
        foreach (byte value in expected.Pixels.Span) if (value > 32 && value < 255) visible++;
        Assert.True(visible > 1000);
        SceneImages.AssertEqual(expected, Assert.IsType<SceneImage>(animated), tolerance: 2);

        Matrix4x4[][] Reference(float time, bool quantized = false)
        {
            AnimationClip parentSource = quantized ? parentData.CreateClip(parentDefinition) : parentClip;
            AnimationClip childSource = quantized ? childData.CreateClip(childDefinition) : childClip;
            using SkeletonInstance referenceParent = storage.CreateInstance(parentDefinition);
            using SkeletonInstance referenceFirst = storage.CreateInstance(childDefinition, referenceParent);
            using SkeletonInstance referenceSecond = storage.CreateInstance(childDefinition, referenceParent);
            var parentLocal = new BoneTransform[1];
            var childLocal = new BoneTransform[2];
            parentSource.SampleLocal(time, parentLocal); referenceParent.Update(parentLocal, time);
            childSource.SampleLocal(time, childLocal); referenceFirst.Update(childLocal, time);
            childSource.SampleLocal(time * .5f, childLocal); referenceSecond.Update(childLocal, time);
            return [referenceParent.ModelPose.GetSection(0).Transforms.ToArray(),
                referenceFirst.ModelPose.GetSection(1).Transforms.ToArray(), referenceSecond.ModelPose.GetSection(1).Transforms.ToArray()];
        }
    }

    private static SkinBinding GpuBinding(bool right) => new(
        [Matrix4x4.CreateTranslation(right ? .2f : 0, 0, 0), Matrix4x4.CreateTranslation(0, right ? -.2f : 0, 0)], [0, 2]);

    [Fact]
    public async Task Two_characters_current_and_previous_poses_match_cpu_geometry_with_two_user_encodings()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        SceneImage floats = await CaptureAnimatedAsync(content);
        await content.WriteMeshesAsync(EncodePackedInfluences);
        await content.SetShaderAsync(PackedReader, "someengine.tests.skin.packed.v1");
        SceneImage packed = await CaptureAnimatedAsync(content);
        await content.WriteReferenceMeshesAsync();
        await content.SetStaticShaderAsync();
        SceneImage? reference = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.Project.RootPath, 4,
            [new(3, edit => edit.CaptureImage(image => reference = image))]), "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        SceneImage expected = Assert.IsType<SceneImage>(reference);
        int lit = 0;
        foreach (byte value in expected.Pixels.Span) if (value > 32 && value < 255) lit++;
        Assert.True(lit > 1000);
        SceneImages.AssertEqual(expected, floats, tolerance: 2);
        SceneImages.AssertEqual(expected, packed, tolerance: 2);
    }

    private static async Task<SceneImage> CaptureAnimatedAsync(SkinContent content)
    {
        var skeleton = new Skeleton([-1, -1],
            [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton);
        using SkeletonInstance second = storage.CreateInstance(skeleton);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<PanelClip, PanelSampler>(new()), [], -1);
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                PoseAnimation.AddSystems(frame.Systems.Animation);
                Entity firstTarget = PoseAnimation.CreateTarget(frame.World, first);
                Entity secondTarget = PoseAnimation.CreateTarget(frame.World, second);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clip, new(0)));
                using CompiledGraph graph = builder.Compile();
                graph.Play(frame.World, firstTarget);
                PropertyAnimation.SetSpeed(frame.World, graph.Play(frame.World, secondTarget), .5f);
                foreach (Entity entity in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(entity).Qvvs.Position.X > 0;
                    frame.World.Add(entity, new MeshSkin(right ? second : first, Binding(right)));
                }
            },
        };
        SceneImage? image = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 4,
            [new(3, edit => edit.CaptureImage(value => image = value))]), "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        return Assert.IsType<SceneImage>(image);
    }

    private static SkinBinding Binding(bool right) => new(
        [Matrix4x4.CreateTranslation(right ? .25f : 0, 0, 0), Matrix4x4.CreateTranslation(0, right ? -.75f : -1, 0)], [0, 1]);

    private sealed class PanelClip { }
    private readonly struct PanelSampler : IClipSampler<PanelClip>
    {
        public static void SampleLocal(PanelClip _, float time, Span<BoneTransform> destination)
        {
            destination[0].Translation = new(2 * time, 0, 0);
            destination[1].Translation = new(0, 1 + .5f * time, 0);
        }
    }

    // This example material uses two influences; the rendering binding is independent of that choice.
    private static byte[] EncodeFloatInfluences(ClusterVertexAttributes surface, ClusterVertexAttributes skin, bool previous)
    {
        var joints = skin.Attributes.Single(a => a.Name == "JOINTS_0");
        var weights = skin.Attributes.Single(a => a.Name == "WEIGHTS_0");
        byte[] bytes = new byte[surface.Data.Length + joints.VertexCount * 16 + 8];
        surface.Data.AsSpan().CopyTo(bytes);
        for (int vertex = 0; vertex < joints.VertexCount; vertex++)
        {
            Span<byte> row = bytes.AsSpan(surface.Data.Length + vertex * 16);
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)joints.ReadComponent(vertex, 0));
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], (uint)joints.ReadComponent(vertex, 1));
            BinaryPrimitives.WriteSingleLittleEndian(row[8..], (float)weights.ReadComponent(vertex, 0));
            BinaryPrimitives.WriteSingleLittleEndian(row[12..], (float)weights.ReadComponent(vertex, 1));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), (uint)surface.Data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), previous ? 1u : 0u);
        return bytes;
    }

    private static byte[] EncodePackedInfluences(ClusterVertexAttributes surface, ClusterVertexAttributes skin, bool previous)
    {
        var joints = skin.Attributes.Single(a => a.Name == "JOINTS_0");
        var weights = skin.Attributes.Single(a => a.Name == "WEIGHTS_0");
        byte[] bytes = new byte[surface.Data.Length + joints.VertexCount * 8 + 8];
        surface.Data.AsSpan().CopyTo(bytes);
        for (int vertex = 0; vertex < joints.VertexCount; vertex++)
        {
            Span<byte> row = bytes.AsSpan(surface.Data.Length + vertex * 8);
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)joints.ReadComponent(vertex, 0) | (uint)joints.ReadComponent(vertex, 1) << 16);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], (uint)(weights.ReadComponent(vertex, 0) * 65536) | (uint)(weights.ReadComponent(vertex, 1) * 65536) << 16);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), (uint)surface.Data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), previous ? 1u : 0u);
        return bytes;
    }

    private sealed class SkinContent(AssetProject project, string shader) : IDisposable
    {
        private readonly SourceMeta _source = new() { SourceGuid = SourceGuid.New() };
        public AssetProject Project => project;
        private string ShaderPath => Path.Combine(project.RootPath, "assets/Shaders/standard.slang");
        public void Dispose() => Directory.Delete(project.RootPath, recursive: true);

        public static async Task<SkinContent> CreateAsync()
        {
            string engine = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(engine, "SomeEngine.slnx"))) engine = Path.GetDirectoryName(engine)!;
            string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Skin-{Guid.NewGuid():N}");
            string source = Path.Combine(engine, "templates/DefaultProject");
            foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "assets"), "*", SearchOption.AllDirectories))
            {
                string output = Path.Combine(directory, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(file, output);
            }
            Directory.CreateDirectory(Path.Combine(directory, "Game.Build"));
            File.Copy(Path.Combine(source, "Game.Build/Game.Build.csproj"), Path.Combine(directory, "Game.Build/Game.Build.csproj"));
            string shader = await File.ReadAllTextAsync(Path.Combine(directory, "assets/Shaders/standard.slang"));
            var project = new AssetProject(directory, [], sourceFormats: [new ShaderSourceFormat([Path.Combine(engine, "assets/Shaders")])]);
            var result = new SkinContent(project, shader);
            try
            {
                await File.WriteAllTextAsync(result.ShaderPath, result.SkinShader(FloatReader, "someengine.tests.skin.float.v1"));
                await DefaultContent.BuildAsync(directory, engine);
                return new SkinContent(new AssetProject(directory, [], sourceFormats: [new ShaderSourceFormat([Path.Combine(engine, "assets/Shaders")])]), shader);
            }
            catch { result.Dispose(); throw; }
        }

        public async Task SetShaderAsync(string reader, string encoding)
        {
            await File.WriteAllTextAsync(ShaderPath, SkinShader(reader, encoding));
            await project.BuildAsync(ShaderPath.Replace(".slang", ".shader"));
        }
        public async Task SetStaticShaderAsync()
        {
            await File.WriteAllTextAsync(ShaderPath, shader);
            await project.BuildAsync(ShaderPath.Replace(".slang", ".shader"));
        }
        public async Task SetReferenceShaderAsync()
        {
            await File.WriteAllTextAsync(ShaderPath, shader
                .Replace("typedef StaticVertexEval VertexEvaluate;", ReferenceEvaluator + "\ntypedef PanelReference VertexEvaluate;")
                .Replace("typedef StaticPositionEval RasterVertexEvaluate;", "typedef PanelReference RasterVertexEvaluate;"));
            await project.BuildAsync(ShaderPath.Replace(".slang", ".shader"));
        }
        private string SkinShader(string reader, string encoding) => shader
            .Replace("typedef StaticVertexEval VertexEvaluate;", SkinEvaluator.Replace("USER_READER", reader) + "\ntypedef PanelSkin VertexEvaluate;")
            .Replace("typedef StaticPositionEval RasterVertexEvaluate;", "typedef PanelSkin RasterVertexEvaluate;")
            .Replace("#define CLUSTER_POSITION_RASTER 1", "#define CLUSTER_POSITION_RASTER 1\n#define CLUSTER_VERTEX_ENCODING \"someengine.cluster.positions.uint16.v1+" + encoding + "\"");

        public Task WriteMeshesAsync(Func<ClusterVertexAttributes, ClusterVertexAttributes, bool, byte[]> encode) =>
            WriteSceneAsync(async (index, previous, right) =>
            {
                MeshAsset mesh = await ImportPanelAsync(index, Positions);
                ClusterVertexAttributesBlock attributes = await mesh.GetBlockAsync<ClusterVertexAttributesBlock>();
                ClusterSkinInfluencesBlock skin = await mesh.GetBlockAsync<ClusterSkinInfluencesBlock>();
                string encoding = encode == EncodeFloatInfluences ? "someengine.tests.skin.float.v1" : "someengine.tests.skin.packed.v1";
                mesh.SetBlock(new ClusterVertexAttributesBlock
                {
                    VertexIdentity = attributes.VertexIdentity, Encoding = encoding,
                    Clusters = attributes.Clusters.Select((item, cluster) => item with { Data = encode(item, skin.Clusters[cluster], previous).ToImmutableArray() }).ToArray(),
                });
                return mesh;
            });

        public Task WriteReferenceMeshesAsync() => WriteSceneAsync((index, previous, right) =>
        {
            float time = (previous ? .75f : 1f) * (right ? .5f : 1);
            SkinBinding binding = Binding(right);
            Matrix4x4 first = binding.InverseBindMatrices[0] * Matrix4x4.CreateTranslation(2 * time, 0, 0);
            Matrix4x4 second = binding.InverseBindMatrices[1] * Matrix4x4.CreateTranslation(0, 1 + .5f * time, 0);
            var positions = new Vector3[4];
            for (int vertex = 0; vertex < 4; vertex++)
            {
                float weight = vertex < 2 ? .75f : .25f;
                positions[vertex] = Vector3.Transform(Positions[vertex], first) * weight + Vector3.Transform(Positions[vertex], second) * (1 - weight);
            }
            return ImportPanelAsync(index, positions);
        });

        public Task WriteReferenceMeshesAsync(Func<bool, bool, (Matrix4x4 First, Matrix4x4 Second)> transforms) =>
            WriteSceneAsync((index, previous, right) =>
            {
                (Matrix4x4 first, Matrix4x4 second) = transforms(previous, right);
                var positions = new Vector3[4];
                for (int vertex = 0; vertex < 4; vertex++)
                {
                    float weight = vertex < 2 ? .75f : .25f;
                    positions[vertex] = Vector3.Transform(Positions[vertex], first) * weight
                        + Vector3.Transform(Positions[vertex], second) * (1 - weight);
                }
                return ImportPanelAsync(index, positions);
            });

        private async Task WriteSceneAsync(Func<int, bool, bool, Task<MeshAsset>> create)
        {
            using var world = new World();
            for (int index = 0; index < 4; index++)
            {
                bool previous = index % 2 == 1, right = index >= 2;
                using MeshAsset mesh = await create(index, previous, right);
                project.RegisterAsset(await mesh.SaveAsync(Path.Combine(project.RootPath, $"assets/Models/panel-{index}.mesh.asset")));
                Entity entity = world.CreateEntity(new WorldTransform { Qvvs = new(new(right ? .75f : -2.5f, previous ? -1f : 1f, 6), Quaternion.Identity) });
                world.Add(entity, new MeshSource { Mesh = new(mesh.Id), BoundsExpansion = 3 });
            }
            world.CreateEntity(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
                VerticalFieldOfView = 1.1f, NearPlane = .1f, FarPlane = 100 });
            world.CreateEntity(new DirectionalLight(new(.35f, -.85f, .4f), Vector3.One, 3));
            AssetEntry entry = project.Manifest.List(WorldAsset.FileType).Single(item => item.Name == "Default");
            using var scene = new WorldAsset(entry.AssetGuid) { Name = "Default" };
            scene.WriteWorld(world, SceneSerialization.CreateRegistry());
            project.RegisterAsset(await scene.SaveAsync(Path.Combine(project.RootPath, entry.Path)));
        }

        private async Task<MeshAsset> ImportPanelAsync(int index, Vector3[] positions)
        {
            var attributes = new Dictionary<string, VertexStream>
            {
                ["POSITION"] = new(3, positions.SelectMany(v => new double[] { v.X, v.Y, v.Z }).ToArray()),
                ["NORMAL"] = new(3, [0,0,-1, 0,0,-1, 0,0,-1, 0,0,-1]),
                ["TANGENT"] = new(4, [1,0,0,1, 1,0,0,1, 1,0,0,1, 1,0,0,1]),
                ["JOINTS_0"] = new(2, [0,1, 0,1, 0,1, 0,1]),
                ["WEIGHTS_0"] = new(2, [.75,.25, .75,.25, .25,.75, .25,.75]),
            };
            var primitive = new VertexPrimitive(0, 0, [0,2,1, 1,2,3], [0,1,2,3], attributes, []);
            await using var context = new ImportContext(project.RootPath, "panels", _source);
            context.ConfigureAssets<GltfMesh, MeshAsset>(_ => $"panel-{index}", (_, id) => new MeshAsset(id),
                (_, id) => id + ".mesh.asset", static (asset, path, token) => asset.SaveAsync(path, token));
            context.EnableImporter<GltfMesh, MeshAsset>(Guid.NewGuid(), [VertexStreamsBlock.BlockTypeId],
                (_, _, asset, _) => { asset.SetBlock(new VertexStreamsBlock([primitive], [])); return ValueTask.CompletedTask; });
            var services = new ServiceCollection();
            ProjectAssetCapabilities.AddAssetCapabilities(services);
            ServiceProvider provider = services.BuildServiceProvider();
            context.Hold(provider);
            var set = provider.GetServices<AssetImporterSetCapability>().Single(value => value.Id == new Guid(GltfImporterSets.ClusterId));
            foreach (var capability in provider.GetServices<AssetImporterCapability>().Where(value => set.Importers.Contains(value.Id)
                || value.Id == new Guid("334ba003-337f-4298-ae72-1a5ce4861591")))
                context.EnableImporter<GltfMesh, MeshAsset>(capability.Bind<GltfMesh>(provider));
            GltfMesh source = ModelRoot.CreateModel().CreateMesh($"panel-{index}");
            source.CreatePrimitive();
            MeshAsset mesh = await context.GetAssetAsync<GltfMesh, MeshAsset>(source);
            mesh.SetBlock(new MeshMaterialsBlock { Materials = [new(project.Manifest.List(MaterialAsset.FileType).Single(value => value.Name == "Standard").AssetGuid)] });
            return mesh.Duplicate();
        }
    }

    private static readonly Vector3[] Positions = [new(-.5f,-.5f,0), new(.5f,-.5f,0), new(-.5f,.5f,0), new(.5f,.5f,0)];
    private const string FloatReader = "uint2 joints = ctx.pageHeap.Load2(address + ctx.localVertexIndex * 16u); float2 weights = asfloat(ctx.pageHeap.Load2(address + ctx.localVertexIndex * 16u + 8u));";
    private const string PackedReader = "uint2 packed = ctx.pageHeap.Load2(address + ctx.localVertexIndex * 8u); uint2 joints = uint2(packed.x & 65535u, packed.x >> 16u); float2 weights = float2(packed.y & 65535u, packed.y >> 16u) / 65536.0;";
    // This example keeps the same full-Vertex raster specialization for the CPU-deformed reference geometry.
    private const string ReferenceEvaluator = """
        struct PanelReference : IVertexEvaluate
        {
            typedef StandardVertex Vertex;
            Vertex evaluate(VertexEvalContext ctx)
            {
                StaticVertexEval source;
                Vertex result = source.evaluate(ctx);
                result.position = TransformInstanceLocalPosition(EvalObjectPosition(ctx), ctx.transform);
                result.basePosition = result.position;
                return result;
            }
            float3 getPosition(Vertex value) { return value.position; }
            Vertex readLane(Vertex value, uint lane) { StaticVertexEval source; return source.readLane(value, lane); }
        };
        """;
    private const string SkinEvaluator = """
        #include "skin_pose.slang"
        struct PanelSkin : IVertexEvaluate
        {
            typedef StandardVertex Vertex;
            Vertex evaluate(VertexEvalContext ctx)
            {
                uint2 footer = ctx.pageHeap.Load2(ctx.attributeBlobAddress + ctx.attributeBlobSize - 8u);
                uint address = ctx.attributeBlobAddress + footer.x;
                USER_READER
                uint2 binding = LoadSkinBinding(ctx.instanceID);
                bool previous = ctx.IsPreviousFrame != 0u || footer.y != 0u;
                float3 position = EvalObjectPosition(ctx);
                float3 local = mul(float4(position, 1), LoadSkinTransform(binding, joints.x, previous)).xyz * weights.x
                    + mul(float4(position, 1), LoadSkinTransform(binding, joints.y, previous)).xyz * weights.y;
                StaticVertexEval source;
                Vertex result = source.evaluate(ctx);
                result.position = TransformInstanceLocalPosition(local, ctx.transform);
                result.basePosition = result.position;
                return result;
            }
            float3 getPosition(Vertex value) { return value.position; }
            Vertex readLane(Vertex value, uint lane) { StaticVertexEval source; return source.readLane(value, lane); }
        };
        """;
}
