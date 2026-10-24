using Game.Content;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Runtime;

namespace Game.App;

/// <summary>The crowd example's ordinary Game-world controls and skeleton instances.</summary>
public sealed class CrowdScene(CrowdExample example) : IDisposable
{
    private readonly SkeletonInstance[] _characters = new SkeletonInstance[CrowdExample.CharacterCount];
    private readonly Entity[] _targets = new Entity[CrowdExample.CharacterCount];
    private readonly List<CrowdPoseSample> _poses = [];
    private CrowdControls _controls = null!;
    public IReadOnlyList<Entity> Targets => _targets;
    public IReadOnlyList<CrowdPoseSample> Poses => _poses;

    public ApplicationScene CreateApplication(string contentDirectory, Action<ApplicationFrameTiming>? measured = null) => new(contentDirectory)
    {
        LodThreshold = 1,
        DeformCacheBytes = 256u * 1024 * 1024,
        Initialize = Initialize,
        UpdateFrame = frame =>
        {
            if (frame.Index != 8) return;
            foreach (int character in new[] { 0, 35, 70, 105 })
                frame.Scene.ReadGpuPose(_targets[character], pose => _poses.Add(new(character, pose)));
        },
        FrameMeasured = measured,
        DescribeWorkload = Describe,
    };

    private void Initialize(ApplicationFrame frame)
    {
        Dispose();
        _poses.Clear();
        var storage = new PoseStorage();
        GpuPoseAnimation.AddSystems(frame.Systems.Animation);
        _controls = new CrowdControls();
        frame.Systems.AddGameSystem(_controls);
        using var builder = new AnimationBuilder(frame.World);
        builder.Leaf<GpuStateParameters, GpuStateLeaf>(Entity.Null, new(example.Machine));
        using CompiledGraph graph = builder.Compile();
        int index = 0;
        foreach (Entity mesh in frame.Scene.MeshEntities)
        {
            SkeletonInstance skeleton = _characters[index] = storage.CreateInstance(example.Skeleton);
            Entity target = _targets[index] = GpuPoseAnimation.CreateTarget(frame.World, skeleton);
            Entity animation = graph.Play(frame.World, target);
            Timeline clock = frame.World.Read<Timeline>(animation);
            clock.Time = CrowdExample.Phase(index);
            frame.World.Replace(animation, clock);
            frame.World.Add(animation, new CrowdCharacter(index));
            frame.World.Add(mesh, new MeshSkin(skeleton, example.Binding));
            index++;
        }
    }

    public object Describe() => new
    {
        Fixture = "Procedural articulated character example for the general pose/material APIs",
        Characters = CrowdExample.CharacterCount, BonesPerCharacter = CrowdExample.BoneCount,
        SourceVerticesPerCharacter = CrowdExample.VertexCount, SourceTrianglesPerCharacter = CrowdExample.TriangleCount,
        Influences = 4, BoneHz = 60, ClipHz = 60, ClipSeconds = 2, States = CrowdExample.StateNames,
        PhaseSeconds = new { Minimum = CrowdExample.Phase(0), Maximum = CrowdExample.Phase(CrowdExample.CharacterCount - 1) },
        TransitionSeconds = .3, ControlCycleSeconds = 1.25, CharacterCycleOffsetSeconds = .037,
        CacheBytesPerSelectedVertex = 48, Width = 1920, Height = 1080, GeometryLodThreshold = 1,
        DeformCacheCapacityBytes = 256u * 1024 * 1024,
        LastGameUpdateSeconds = _controls.Seconds, RequestedStateCounts = _controls.Counts,
        Camera = new { Position = new[] { 0f, 30, -35 }, Target = new[] { 0f, .8f, 20 }, VerticalFieldOfView = .8f,
            NearPlane = 40, FarPlane = 90 },
        Material = new { Surface = "StandardMaterial", Roughness = .5f, MetallicFactor = 0,
            BaseColorTint = new[] { 1f, 1, 1, 1 }, EmissiveFactor = new[] { 0f, 0, 0 } },
        DirectionalLight = new { Direction = new[] { .35f, -.85f, .4f }, Color = new[] { 1f, .96f, .9f }, Intensity = 3 },
        PoseSamples = _poses.Select(sample => new
        {
            sample.Character, sample.Pose.InputNumber, sample.Pose.CalculationTime, sample.Pose.SampleTime,
            sample.Pose.Playback, Bones = sample.Pose.Current.Length,
        }).ToArray(),
    };

    public void Dispose()
    {
        foreach (SkeletonInstance? character in _characters) character?.Dispose();
        Array.Clear(_characters);
    }

    private readonly record struct CrowdCharacter(int Index) : IComponent;

    private sealed class CrowdControls : ISystem<ImmediateSystemContext>
    {
        private QueryHandle _characters;
        public double Seconds { get; private set; }
        public int[] Counts { get; } = new int[4];
        public void OnCreate(ref ImmediateSystemContext context) => _characters = context.World.Query(
            new QueryDefinitionBuilder().Read<CrowdCharacter>().WriteBuffer<GpuStateInstance>());
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Seconds += context.DeltaTime;
            Array.Clear(Counts);
            CrowdControls controls = this;
            context.World.ExecuteQuery(_characters, ref controls, static (QueryCursor rows, ref CrowdControls controls) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    int state = CrowdExample.RequestedState(row.Read<CrowdCharacter>().Index, controls.Seconds);
                    row.Buffer<GpuStateInstance>()[0].Controls[0] = state;
                    controls.Counts[state]++;
                }
            });
        }
        public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_characters);
    }
}

public sealed record CrowdPoseSample(int Character, GpuPoseReadback Pose);
