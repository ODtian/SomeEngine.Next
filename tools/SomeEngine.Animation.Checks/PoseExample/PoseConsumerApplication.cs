using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Runtime;

namespace SomeEngine.Animation.Checks.PoseExample;

internal static class PoseConsumerApplication
{
    internal static async Task RunAsync(string directory, string imagePath)
    {
        PoseConsumerChecks.Check(!RuntimeFeature.IsDynamicCodeSupported, "Run this flow with the separately published NativeAOT executable.");
        AnimationClip clip = await PoseConsumerChecks.RunAsync(directory);
        PoseConsumerFlow? flow = null;
        SceneImage? image = null;
        bool rendered = false;
        var application = new ApplicationScene(directory)
        {
            Initialize = frame =>
            {
                PoseConsumerFlow.AddSystems(frame.Systems.Animation);
                flow = new(frame.World, clip);
                var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)], [0, 2]);
                foreach (Entity entity in frame.Scene.MeshEntities)
                {
                    bool first = frame.World.Read<WorldTransform>(entity).Qvvs.Position.X < 0;
                    frame.World.Add(entity, new MeshSkin(first ? flow.First : flow.Second, binding));
                }
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) flow!.SwitchFirst(frame.World);
            },
            FrameCompleted = frame =>
            {
                if (frame.Index != 7) return;
                PoseConsumerFlow.Near(new(.45f, 1, 0), flow!.First.ModelPose.GetBone(2).Transform.Translation);
                PoseConsumerFlow.Near(new(.4f, 1, .2f), flow.Second.ModelPose.GetBone(2).Transform.Translation);
                flow.CheckPrediction();
                Console.WriteLine($"POSE_CONSUMER_APP frame={frame.Index} first={flow.First.ModelPose.GetBone(2).Transform.Translation} second={flow.Second.ModelPose.GetBone(2).Transform.Translation} adapter={frame.Adapter.Name}");
            },
            FrameRendered = metrics => rendered = metrics.Work.Complete &&
                metrics.PhaseOneSoftwareClusters + metrics.PhaseOneHardwareClusters +
                metrics.PhaseTwoSoftwareClusters + metrics.PhaseTwoHardwareClusters > 0,
            Shutdown = _ => flow!.Dispose(),
        };
        var script = new SceneScript(application, 8, [new(7, edit => edit.CaptureImage(value => image = value))]);
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", "d3d12", "--no-taa", "--no-vsm", "--fixed-step", ".125"], script);
        PoseConsumerChecks.Check(result.Succeeded, $"Application result: {result.Failure}; {result.Error}");
        int visible = 0;
        for (int pixel = 0; pixel < image!.Pixels.Length; pixel += 4)
            if (image.Pixels.Span[pixel] > 32) visible++;
        PoseConsumerChecks.Check(rendered && visible > 1000, "The completed Cluster frame must contain visible skinned panels.");
        image.SaveBmp(imagePath);
        Console.WriteLine($"POSE_CONSUMER_NATIVE nativeAot={!RuntimeFeature.IsDynamicCodeSupported} frames={result.CompletedFrames} visiblePixels={visible} image={imagePath}");
    }
}
