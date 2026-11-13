using System.Collections.Concurrent;
using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Physics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationPhysicsTests(SceneContentFixture content)
{
    [Fact]
    public async Task ApplicationInputsReachBodyTransformsAndTheExistingRenderingWorld()
    {
        var rendering = new BodyTransformReader();
        PhysicsFrame<PredictBodies, GameFrameTests.CopyBodyTransforms>? physics = null;
        Query copy = default;
        Entity body = default;
        var times = new List<double>();
        float finalPosition = 0;
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"])
                with { FixedFrameSeconds = 1 },
            Initialize = frame =>
            {
                World world = frame.World;
                Query meshes = world.Query(new QueryDescription().WithStored<MeshInstance>());
                Entity prototype = default;
                using (QueryScope rows = meshes.OpenRead())
                    foreach (QueryChunk chunk in rows) { prototype = chunk.Entities[0]; break; }
                body = frame.Scene.AddEntity(prototype, TransformQvvs.Identity);
                rendering.Source = body;
                world.Add(body, new BodyPose { Rotation = Quaternion.Identity });
                world.Add(body, new BodyVelocity { Linear = Vector3.UnitX });
                world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
                world.Add(body, new BodyStepState());
                world.Add(body, new LocalTransform { Value = TransformQvvs.Identity });
                world.Add(body, new ApplicationPhysicsBody());
                Query bodies = world.Query(new QueryDescription().With<ApplicationPhysicsBody>());
                copy = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<LocalTransform>());
                var simulation = PhysicsSimulation.Create(world, bodies, new PredictBodies(Vector3.Zero, 64), 1);
                physics = simulation.CreateFrame(new GameFrameTests.CopyBodyTransforms(copy, new ConcurrentQueue<string>()),
                    stepDuration: 1, maximumDelay: 0, firstStepNumber: 1, startTime: 0);
                frame.Systems.Physics = physics;
            },
            UpdateFrame = frame => frame.World.Set(body,
                new BodyVelocity { Linear = new Vector3(frame.Index + 1, 0, 0) }),
            FrameCompleted = _ => times.Add(physics!.Result.EndTime),
            Shutdown = frame =>
            {
                finalPosition = frame.World.ReadComponentData<BodyPose>(body).Position.X;
                
            },
        };
        scene.InstallGeometry(rendering);

        ApplicationResult result = await Application.RunAsync([], scene);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([1f, 3, 6], rendering.Positions);
        Assert.Equal([1d, 2, 3], times);
        Assert.Equal(6, finalPosition);
    }

    private readonly struct ApplicationPhysicsBody : IComponent;

    private sealed class BodyTransformReader : IApplicationGeometryFeature, ISystem<RenderFrameSystemContext>
    {
        private Query _query;
        internal Entity Source;
        internal readonly List<float> Positions = [];
        public IDisposable? Install(ApplicationGeometryContext context)
        {
            context.AddFrameSystem(this);
            return null;
        }
        public void OnCreate(ref RenderFrameSystemContext context) =>
            _query = context.World.World.Query(new QueryDescription().With<RenderSource>().With<RenderTransform>());
        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            using QueryScope rows = _query.OpenRead();
            foreach (QueryChunk chunk in rows)
            {
                ReadOnlySpan<RenderSource> sources = chunk.Read<RenderSource>();
                ReadOnlySpan<RenderTransform> transforms = chunk.Read<RenderTransform>();
                for (int row = 0; row < chunk.Count; row++)
                    if (sources[row].Entity == Source) Positions.Add(transforms[row].Position.X);
            }
        }
        public void OnDestroy(ref RenderFrameSystemContext context) { }
    }
}
