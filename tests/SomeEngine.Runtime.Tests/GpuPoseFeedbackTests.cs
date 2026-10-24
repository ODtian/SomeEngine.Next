using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task SelectedGpuBonesKeepTheirExecutionIdentityAcrossRestartAndReleaseTheWaitingStage()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform identity = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        var definition = new Skeleton([-1, 0, 1], [identity, identity, identity], [1, 2, 3]);
        QuantizedClipData clip = QuantizedClipData.Encode(GpuPoseExamples.Clip(definition, 0), 4);
        var storage = new PoseStorage();
        using SkeletonInstance body = storage.CreateInstance(definition);
        var trace = new FeedbackTrace();
        Entity target = default, animation = default;
        CompiledGraph graph = default;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                AnimationSystems.Add(frame.Systems.Animation, new FeedbackNodeSystem(), AnimationPhase.Composition);
                AnimationSystems.Add(frame.Systems.Animation, new ReadFeedbackStages(trace), AnimationPhase.Completion);
                target = GpuPoseAnimation.CreateTarget(frame.World, body);
                using var builder = new AnimationBuilder(frame.World);
                using (builder.Sequence())
                {
                    using (builder.Parallel())
                    {
                        builder.Leaf<FeedbackParameters, FeedbackLeaf>(target, new(clip, trace));
                        builder.Delay(.25f);
                    }
                    builder.Delay(.5f);
                }
                graph = builder.Compile();
                animation = graph.Play(frame.World);
                trace.Animation = animation;
                foreach (Entity mesh in frame.Scene.MeshEntities)
                    frame.World.Add(mesh, new MeshSkin(body, new SkinBinding([Matrix4x4.Identity, Matrix4x4.Identity], [1, 2])));
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 1)
                {
                    PropertyAnimation.Stop(frame.World, animation);
                    animation = graph.Play(frame.World);
                    trace.Animation = animation;
                }
            },
        };
        try
        {
            await SceneImages.RunAsync("d3d12", new SceneScript(application, 8, []), "--fixed-step", "0.25", "--no-taa", "--no-vsm");
        }
        finally { graph.Dispose(); }
        Assert.Equal(2, trace.Requests.Count);
        Assert.Equal(1, trace.StaleResults);
        Assert.Equal(1, trace.AcceptedResults);
        Assert.Equal(1u, trace.Stages[^1].Completions);
        Assert.Contains(trace.Stages, stage => stage.Stage == 1 && stage.Time == .25f);
        GpuPoseReadbackRequest first = trace.Requests[0], second = trace.Requests[1];
        Assert.Equal(first.Identity.Starts, second.Identity.Starts);
        Assert.Equal(first.Identity.AnimationStarts + 1, second.Identity.AnimationStarts);
        Assert.True(second.Identity.InputNumber > first.Identity.InputNumber);
        using SkeletonInstance reference = storage.CreateInstance(definition);
        var local = new BoneTransform[3];
        AnimationClip cpuClip = clip.CreateClip(definition);
        foreach (GpuPoseReadbackRequest request in trace.Requests)
        {
            Assert.True(request.Completion.IsCompletedSuccessfully);
            GpuPoseReadback result = await request.Completion;
            Assert.Equal(request.Identity, result.Identity);
            Assert.Same(body, result.Identity.Instance);
            Assert.Equal(target, result.Identity.Target);
            Assert.Equal(new PoseRange(1, 2), result.Range);
            Assert.Equal(2, result.Current.Length);
            Assert.Equal(2, result.Previous.Length);
            Assert.Equal(.25f, result.SampleTime);
            Assert.Equal(request == first ? .25 : .5, result.CalculationTime);
            Assert.Equal(result.CalculationTime, result.SourceCalculationTime);
            cpuClip.SampleLocal(result.SampleTime, local);
            reference.Update(local, result.CalculationTime);
            for (int bone = 0; bone < 2; bone++)
            {
                GpuPoseExamples.Near(reference.ModelPose.GetBone(bone + 1).Transform, result.Current.Span[bone]);
                GpuPoseExamples.Near(reference.ModelPose.GetBone(bone + 1).Transform, result.Previous.Span[bone]);
                Assert.Equal(result.CalculationTime, result.Calculations.Span[bone].Time);
                Assert.Equal(.25, result.Calculations.Span[bone].SampleTime);
            }
        }
    }

    private sealed class FeedbackTrace
    {
        internal Entity Animation;
        internal readonly List<GpuPoseReadbackRequest> Requests = [];
        internal readonly List<(ushort Stage, float Time, uint Completions)> Stages = [];
        internal int StaleResults, AcceptedResults;
    }
    private readonly record struct FeedbackParameters(QuantizedClipData Clip, FeedbackTrace Trace, float MinimumX = .1f);
    [BufferCapacity(1)]
    private struct FeedbackNode : IBufferElement
    {
        internal FeedbackParameters Parameters;
        internal AnimationLeafPlacement Placement;
        internal Entity Target;
        internal GpuPoseReadbackRequest? Request;
    }
    // This consumer leaf is an example of a result-driven algorithm using the common lifecycle.
    private readonly struct FeedbackLeaf : IAnimationLeaf<FeedbackParameters>
    {
        public static MotionBoundary Describe(in FeedbackParameters parameters) => new(StageEnd.Settled);
        public static void Install(World world, Entity animation, Entity target,
            AnimationLeafBatch<FeedbackParameters> definitions, int index, in AnimationLeafPlacement placement)
        {
            if (!world.HasBuffer<FeedbackNode>(animation)) world.AddBuffer<FeedbackNode>(animation);
            var node = new FeedbackNode { Parameters = definitions.Parameters[index], Placement = placement, Target = target };
            world.ExecuteBufferWrite<FeedbackNode, FeedbackNode>(animation, ref node, static (nodes, ref node) => nodes.Add(node));
        }
        public static void Release(World world, Entity animation) =>
            world.ExecuteBufferWrite<FeedbackNode>(animation, static nodes => nodes.Clear());
    }
    private struct FeedbackNodeSystem : ISystem<ImmediateSystemContext>
    {
        private QueryHandle _nodes;
        public void OnCreate(ref ImmediateSystemContext context) => _nodes = context.World.Query(AnimationGraph.WithTimeAccess(
            context.World.QueryDefinition().ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<FeedbackNode>().Optional<GpuPoseTarget>(QueryAccess.ReadWrite)));
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            World world = context.World;
            world.ExecuteQuery(_nodes, ref world, static (QueryCursor rows, ref World world) =>
            {
                foreach (QueryRow row in rows.Rows)
                foreach (ref FeedbackNode node in row.Buffer<FeedbackNode>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, row.Entity, in node.Placement);
                    if (!time.Active) continue;
                    GpuPoseTarget output = world.Read<GpuPoseTarget>(node.Target);
                    output.Clip = node.Parameters.Clip;
                    output.SampleTime = time.Time.Time;
                    output.Starts = time.Starts;
                    output.AnimationStarts = node.Placement.Starts;
                    world.Replace(node.Target, output);
                    if (time.Time.Time == 0) { AnimationGraph.Submit(world, row.Entity, in node.Placement, true); continue; }
                    FeedbackTrace trace = node.Parameters.Trace;
                    if (node.Request is null)
                    {
                        if (trace.Requests.Count > 0)
                        {
                            if (!GpuPoseAnimation.SubmitFeedback(world, row.Entity, node.Target, in node.Placement, trace.Requests[0], finished: true))
                                trace.StaleResults++;
                        }
                        node.Request = GpuPoseAnimation.RequestReadback(world, node.Target, new PoseRange(1, 2));
                        trace.Requests.Add(node.Request);
                    }
                    bool finished = node.Request.Completion.IsCompletedSuccessfully
                        && node.Request.Completion.Result.Current.Span[1].M41 >= node.Parameters.MinimumX;
                    if (GpuPoseAnimation.SubmitFeedback(world, row.Entity, node.Target, in node.Placement, node.Request, finished))
                        trace.AcceptedResults++;
                }
            });
        }
        public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_nodes);
    }
    private sealed class ReadFeedbackStages(FeedbackTrace trace) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Timeline time = context.World.Read<Timeline>(trace.Animation);
            trace.Stages.Add((time.Stage, time.Time, time.Completions));
        }
    }
}
