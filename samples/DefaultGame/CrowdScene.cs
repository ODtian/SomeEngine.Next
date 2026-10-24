using System.Text.Json.Nodes;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Runtime;

namespace DefaultGame;

/// <summary>The crowd example's ordinary Game-world controls and skeleton instances.</summary>
public sealed class CrowdScene(CrowdExample example) : IDisposable
{
    private readonly SkeletonInstance[] _characters = new SkeletonInstance[CrowdExample.CharacterCount];
    private readonly Entity[] _targets = new Entity[CrowdExample.CharacterCount];
    private readonly List<(int Character, GpuPoseReadbackRequest Request)> _poseRequests = [];
    private CrowdControls _controls = null!;
    public IReadOnlyList<Entity> Targets => _targets;
    public IReadOnlyList<CrowdPoseSample> Poses => _poseRequests
        .Where(sample => sample.Request.Completion.IsCompletedSuccessfully)
        .Select(sample => new CrowdPoseSample(sample.Character, sample.Request.Completion.Result)).ToArray();

    public ApplicationScene CreateApplication(string contentDirectory, Action<ApplicationFrameTiming>? measured = null) => new(contentDirectory)
    {
        LodThreshold = 1,
        DeformCacheBytes = 256u * 1024 * 1024,
        Initialize = Initialize,
        FrameMeasured = measured,
    };

    public TestScene CreateScene(string name, string contentDirectory) => new(name, contentDirectory)
    {
        Initialize = Initialize, DescribeWorkload = Describe,
        MeasurementFrames = new(64, 128), DeformCacheBytes = 256u * 1024 * 1024,
    };

    private void Initialize(ApplicationFrame frame)
    {
        Dispose();
        _poseRequests.Clear();
        var storage = new PoseStorage();
        GpuPoseAnimation.AddSystems(frame.Systems.Animation);
        _controls = new CrowdControls();
        frame.Systems.AddGameSystem(_controls);
        AnimationSystems.Add(frame.Systems.Animation, new CrowdReadbacks(_targets, _poseRequests, _controls), AnimationPhase.Completion);
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

    public JsonNode Describe() => new JsonObject
    {
        ["Fixture"] = "Procedural articulated character example for the general pose/material APIs",
        ["Characters"] = CrowdExample.CharacterCount, ["BonesPerCharacter"] = CrowdExample.BoneCount,
        ["SourceVerticesPerCharacter"] = CrowdExample.VertexCount, ["SourceTrianglesPerCharacter"] = CrowdExample.TriangleCount,
        ["Influences"] = 4, ["BoneHz"] = 60, ["ClipHz"] = 60, ["ClipSeconds"] = 2,
        ["States"] = new JsonArray(CrowdExample.StateNames.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["PhaseSeconds"] = new JsonObject { ["Minimum"] = CrowdExample.Phase(0), ["Maximum"] = CrowdExample.Phase(CrowdExample.CharacterCount - 1) },
        ["TransitionSeconds"] = .3, ["ControlCycleSeconds"] = 1.25, ["CharacterCycleOffsetSeconds"] = .037,
        ["CacheBytesPerSelectedVertex"] = 48, ["Width"] = 1920, ["Height"] = 1080, ["GeometryLodThreshold"] = 1,
        ["DeformCacheCapacityBytes"] = 256u * 1024 * 1024,
        ["LastGameUpdateSeconds"] = _controls.Seconds,
        ["RequestedStateCounts"] = new JsonArray(_controls.Counts.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["Camera"] = new JsonObject { ["Position"] = new JsonArray(0, 30, -35), ["Target"] = new JsonArray(0, .8f, 20),
            ["VerticalFieldOfView"] = .8f, ["NearPlane"] = 40, ["FarPlane"] = 90 },
        ["Material"] = new JsonObject { ["Surface"] = "StandardMaterial", ["Roughness"] = .5f, ["MetallicFactor"] = 0,
            ["BaseColorTint"] = new JsonArray(1, 1, 1, 1), ["EmissiveFactor"] = new JsonArray(0, 0, 0) },
        ["DirectionalLight"] = new JsonObject { ["Direction"] = new JsonArray(.35f, -.85f, .4f),
            ["Color"] = new JsonArray(1, .96f, .9f), ["Intensity"] = 3 },
        ["PoseSamples"] = new JsonArray(Poses.Select(sample => (JsonNode)new JsonObject
        {
            ["Character"] = sample.Character, ["InputNumber"] = sample.Pose.InputNumber,
            ["CalculationTime"] = sample.Pose.CalculationTime, ["SampleTime"] = sample.Pose.SampleTime,
            ["Bones"] = sample.Pose.Current.Length,
            ["Playback"] = new JsonObject
            {
                ["State"] = sample.Pose.Playback.State, ["SourceState"] = sample.Pose.Playback.SourceState,
                ["Time"] = sample.Pose.Playback.Time, ["SourceTime"] = sample.Pose.Playback.SourceTime,
                ["SourceSampleTime"] = sample.Pose.Playback.SourceSampleTime,
                ["TransitionWeight"] = sample.Pose.Playback.TransitionWeight, ["Finished"] = sample.Pose.Playback.Finished,
            },
        }).ToArray()),
    };

    public void Dispose()
    {
        foreach (SkeletonInstance? character in _characters) character?.Dispose();
        Array.Clear(_characters);
    }

    private readonly record struct CrowdCharacter(int Index) : IComponent;

    private sealed class CrowdReadbacks(Entity[] targets,
        List<(int Character, GpuPoseReadbackRequest Request)> requests, CrowdControls controls) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            if (requests.Count != 0 || controls.Seconds < 8d / 60) return;
            foreach (int character in new[] { 0, 35, 70, 105 })
                requests.Add((character, GpuPoseAnimation.RequestReadback(context.World, targets[character], new(0, CrowdExample.BoneCount))));
        }
    }

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
            context.World.ExecuteQuery(_characters, ref controls, static (scoped QueryCursor rows, ref CrowdControls controls) =>
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
