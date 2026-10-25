using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.Physics.Research;

// The same numerical settings apply to every scene selected by this run.
// Scene descriptions contain physical inputs and observations; the runner owns
// iteration count, substeps, sleeping and the complete-step timing protocol.
internal static unsafe class PhysicsAcceptance
{
    internal static int Run(IReadOnlyDictionary<string, string> options)
    {
        int iterations = options.TryGetValue("iterations", out string? iterationOption)
            ? int.Parse(iterationOption) : PhysicsDemo.DefaultRigidIterations;
        int substeps = int.Parse(options.GetValueOrDefault("substeps", "1"));
        int frames = int.Parse(options.GetValueOrDefault("frames", "360"));
        string mode = options.GetValueOrDefault("mode", "both");
        string label = options.GetValueOrDefault("label", "current");
        string? native = options.GetValueOrDefault("native");
        string selected = options.GetValueOrDefault("scenes", "all");
        PhysicsSceneDescription[] scenes = PhysicsScenes.All.Where(scene => selected == "all" || selected.Split(',').Contains(scene.Id)).ToArray();
        int failures = 0;
        var json = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        foreach (PhysicsSceneDescription scene in scenes)
        {
            PhysicsDemo Create()
            {
                var world = new PhysicsDemo(scene.Dimension, scene.Scene, iterations, PhysicsSolver.LongRangeConstraints, null,
                    nativeDirectory: native) { SleepingEnabled = false, Substeps = substeps };
                if (scene.DisturbanceFrame >= 0) world.AddEvent(Research.Disturbance(scene, world.BodyCount));
                return world;
            }

            List<double> times = [], used = [];
            double[] measurements = [];
            int bodyCount = 0;
            if (mode is "both" or "timing" or "profile")
            {
                using (var warmup = Create()) for (int frame = 0; frame < 180; ++frame) warmup.Step();
                for (int repeat = 0; repeat < (mode == "profile" ? 1 : 3); ++repeat)
                {
                    using var world = Create();
                    bodyCount = world.BodyCount;
                    for (int frame = 0; frame < frames; ++frame)
                    {
                        long begin = Stopwatch.GetTimestamp();
                        world.Step();
                        times.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                        used.Add(world.ReadIterations());
                    }
                    if (mode == "profile") measurements = world.ReadStepMeasurements();
                }
            }

            double initial = 0, peak = 0, joint = 0;
            int peakFrame = -1, peakIterations = 0, first = -1, second = -1, contactPairs = 0;
            bool finite = true;
            int firstInvalidFrame = -1;
            List<double> depths = [];
            if (mode is "both" or "accuracy")
            {
                using var world = Create();
                bodyCount = world.BodyCount;
                PhysicsInput input = world.ReadInput();
                var bodies = new BodyFrame[bodyCount];
                var geometry = new BoxPenetration(bodyCount, scene.Dimension, input.CollisionExclusions);
                world.ReadFrames(bodies); initial = geometry.Read(bodies) * 1000;
                for (int frame = 0; frame < frames; ++frame)
                {
                    world.Step();
                    world.ReadFrames(bodies);
                    foreach (BodyFrame body in bodies)
                    {
                        for (int axis = 0; axis < 3; ++axis) finite &= double.IsFinite(body.Position[axis]);
                        for (int axis = 0; axis < 4; ++axis) finite &= double.IsFinite(body.Rotation[axis]);
                    }
                    if (!finite && firstInvalidFrame < 0) firstInvalidFrame = frame;
                    double depth = geometry.Read(bodies) * 1000;
                    depths.Add(depth);
                    contactPairs = Math.Max(contactPairs, geometry.Contacts);
                    if (depth > peak) { peak = depth; peakFrame = frame; peakIterations = world.ReadIterations(); first = geometry.First; second = geometry.Second; }
                    if (input.Joints.Length > 0) joint = Math.Max(joint, world.ReadJointError() * 1000);
                    if (mode == "accuracy") used.Add(world.ReadIterations());
                }
                if (!finite || peak > 5) ++failures;
            }
            times.Sort(); depths.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                label, scene = scene.Id, scene.Family, scene.Trigger, scene.Measurement,
                iterations, substeps, sleeping = false, frames, bodyCount, finite, firstInvalidFrame,
                initial, peak, peakFrame, peakIterations, first, second, joint, contactPairs,
                p95Depth = depths.Count == 0 ? 0 : depths[(int)(depths.Count * .95)],
                mean = times.Count == 0 ? 0 : times.Average(),
                p95 = times.Count == 0 ? 0 : times[(int)(times.Count * .95)],
                timedFrames = times.Count,
                iterationMean = used.Count == 0 ? 0 : used.Average(),
                iterationMax = used.Count == 0 ? 0 : used.Max(), measurements
            }, json));
        }
        return failures == 0 ? 0 : 1;
    }
}
