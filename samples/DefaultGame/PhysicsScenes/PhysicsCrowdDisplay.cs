using System.Numerics;
using System.Text.Json.Nodes;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Graphics;
using SomeEngine.Physics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Resources;
using SomeEngine.Job;
using SomeEngine.Resources.Geometry;
using SomeEngine.Runtime;

namespace DefaultGame;

/// <summary>Selected physics characters, bone cloth and rigid meshes through the ordinary Game-world components.</summary>
internal sealed class PhysicsCrowdDisplay(StandardMaterial material, Shader skinShader, PhysicsCrowdScale scale) : IApplicationGeometryFeature
{
    private IGraphicsBackend _backend = null!;
    private Device _device = null!;
    private JsonObject _workload = new();

    public TestScene CreateScene(string contentDirectory) => new(scale.Name, contentDirectory)
    {
        Initialize = Initialize, DescribeWorkload = Describe,
        WorldNameplates = false, MeasurementFrames = new(32, 180),
    };

    private JsonNode Describe()
    {
        _workload["JobResourceStateHighWater"] = JobSystem.GetStats().ResourceStateHighWater;
        return _workload;
    }

    IDisposable? IApplicationGeometryFeature.Install(ApplicationGeometryContext context)
    {
        _backend = context.Backend; _device = context.Device;
        return null;
    }

    private void Initialize(ApplicationFrame frame)
    {
        Entity[] previousMeshes = frame.Scene.MeshEntities.ToArray();
        GpuPoseAnimation.AddSystems(frame.Systems.Animation);
        PhysicsCrowdExample example = scale.Create(_backend, _device);
        PhysicsCrowdScene scene = example.CreateSimulation(frame.World);
        frame.Systems.Physics = scene;
        _workload = scale.Describe(example, scene, frame.World);
        _workload["Adapter"] = _device.Adapter.Name;
        (Mesh character, ClusterSkinBounds characterBounds) = CreateCharacter(example.Character);
        SkeletonInstance firstCloth = frame.World.Read<GpuPoseTarget>(scene.ClothTargets[0]).Skeleton;
        (Mesh cloth, SkinBinding clothBinding, ClusterSkinBounds clothBounds) = CreateCloth(firstCloth);
        StandardMaterial characterMaterial = Color(new(.9f, .3f, .06f, 1), skinShader);
        StandardMaterial clothMaterial = Color(new(.04f, .65f, .9f, 1), skinShader);
        for (int index = 0; index < example.CharacterCount; index++)
        {
            GpuPoseTarget target = frame.World.Read<GpuPoseTarget>(scene.CharacterTargets[index]);
            AddSkin(target, character, example.Character.Binding, characterBounds, characterMaterial);
            target = frame.World.Read<GpuPoseTarget>(scene.ClothTargets[index]);
            AddSkin(target, cloth, clothBinding, clothBounds, clothMaterial);
        }
        Mesh rigidBox = PhysicsSceneExample.CreateBox();
        StandardMaterial fixedMaterial = Color(new(.12f, .16f, .21f, 1), material.Shader);
        StandardMaterial movingMaterial = Color(new(.9f, .3f, .06f, 1), material.Shader);
        int boxes = 0;
        foreach (Entity body in scene.Bodies)
        {
            if (!frame.World.Has<BoxGeometry>(body)) continue;
            BoxGeometry box = frame.World.Read<BoxGeometry>(body);
            BodyPose pose = frame.World.Read<BodyPose>(body);
            bool fixedBody = frame.World.Read<BodyMass>(body).Motion == BodyMotion.Static;
            var transform = new TransformQvvs(pose.Position, pose.Rotation)
                { Stretch = fixedBody ? Vector3.One : box.HalfExtents * 2 };
            frame.World.Add(body, new LocalTransform { Value = transform });
            frame.World.Add(body, new WorldTransform { Qvvs = transform });
            frame.World.Add(body, new MeshInstance { Mesh = fixedBody ? CreateFloor(box.HalfExtents) : rigidBox });
            SetMaterial(body, fixedBody ? fixedMaterial : movingMaterial);
            boxes++;
        }
        _workload["SkinMeshes"] = scene.CharacterTargets.Count + scene.ClothTargets.Count;
        _workload["BoxMeshes"] = boxes;
        foreach (Entity mesh in previousMeshes) frame.Scene.DeleteEntity(mesh);
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (Entity body in scene.Bodies)
        {
            if (frame.World.Read<BodyMass>(body).Motion == BodyMotion.Static) continue;
            Vector3 position = frame.World.Read<BodyPose>(body).Position;
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        Vector3 center = (minimum + maximum) * .5f;
        float extent = MathF.Max(4, (maximum - minimum).Length());
        Vector3 camera = center + new Vector3(.55f, .35f, .9f) * extent;
        Vector3 forward = Vector3.Normalize(center - camera);
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 up = Vector3.Cross(forward, right);
        frame.Scene.SetCameraTransform(camera, Quaternion.CreateFromRotationMatrix(new(
            right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1)));

        void AddSkin(GpuPoseTarget target, Mesh mesh, SkinBinding binding, ClusterSkinBounds bounds, StandardMaterial surface)
        {
            Entity entity = frame.World.CreateEntity(new WorldTransform { Qvvs = new(target.ModelToWorld.Translation, Quaternion.Identity) });
            frame.World.Add(entity, new MeshInstance { Mesh = mesh });
            frame.World.Add(entity, new MeshSkin(target.Skeleton, binding));
            frame.World.Add(entity, new MeshSkinBounds(bounds));
            SetMaterial(entity, surface);
        }
        void SetMaterial(Entity entity, StandardMaterial surface)
        {
            frame.World.AddBuffer<MeshMaterialBinding>(entity);
            frame.World.ExecuteBufferWrite<MeshMaterialBinding>(entity, materials => materials.Add(new(surface)));
        }
    }

    private StandardMaterial Color(Vector4 color, Shader? shader) => new()
    {
        Shader = shader, AlbedoMap = material.AlbedoMap, NormalMap = material.NormalMap, ARMMap = material.ARMMap,
        MaterialSampler = material.MaterialSampler, BaseColorTint = color, Roughness = .7f,
        EmissiveFactor = new Vector3(color.X, color.Y, color.Z) * .12f,
    };

    internal static (Mesh Mesh, ClusterSkinBounds Bounds) CreateCharacter(CrowdExample example)
    {
        var attributes = new PhysicsSkinAttributes[example.Positions.Length];
        for (int vertex = 0; vertex < attributes.Length; vertex++)
        {
            int first = example.VertexParts[vertex] * 4;
            attributes[vertex] = new(new(example.Normals[vertex], example.Tangents[vertex], Vector2.Zero),
                new(first, first + 1, first + 2, first + 3), new Vector4(102, 77, 51, 25) / 255);
        }
        return PhysicsSkinVertexEncoder.Build(example.Positions, attributes, example.Indices, "Physics character");
    }

    internal static (Mesh Mesh, SkinBinding Binding, ClusterSkinBounds Bounds) CreateCloth(SkeletonInstance skeleton)
    {
        var positions = new Vector3[16];
        var attributes = new PhysicsSkinAttributes[16];
        var inverse = new Matrix4x4[16];
        var bones = new short[16];
        for (int bone = 0; bone < 16; bone++)
        {
            bones[bone] = (short)(skeleton.Skeleton.InheritedBoneCount + bone);
            Matrix4x4 transform = skeleton.ModelPose.Transforms[bones[bone]];
            positions[bone] = transform.Translation;
            Matrix4x4.Invert(transform, out inverse[bone]);
            attributes[bone] = new(new(Vector3.UnitZ, new(1, 0, 0, 1), new((bone / 4) / 3f, bone % 4 / 3f)),
                new(bone, 0, 0, 0), Vector4.UnitX);
        }
        var indices = new List<uint>(54);
        for (uint column = 0; column < 3; column++)
        for (uint row = 0; row < 3; row++)
        {
            uint first = column * 4 + row;
            indices.AddRange([first, first + 1, first + 4, first + 4, first + 1, first + 5]);
        }
        (Mesh mesh, ClusterSkinBounds bounds) = PhysicsSkinVertexEncoder.Build(positions, attributes, indices.ToArray(), "Physics bone cloth");
        return (mesh, new(inverse, bones), bounds);
    }

    private static Mesh CreateFloor(Vector3 half)
    {
        Vector3[] positions = [new(-half.X, half.Y, -half.Z), new(half.X, half.Y, -half.Z),
            new(half.X, half.Y, half.Z), new(-half.X, half.Y, half.Z)];
        StandardClusterAttributes[] attributes = Enumerable.Repeat(new StandardClusterAttributes(Vector3.UnitY,
            new(1, 0, 0, 1), Vector2.Zero), 4).ToArray();
        return ClusterMeshBuilder.Build(positions, attributes, new uint[] { 0, 2, 1, 0, 3, 2 }, [new ClusterMaterialGroup(0, 6, 0)],
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Physics floor");
    }
}
