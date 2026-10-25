using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.Physics.Research;

internal sealed record MethodDescription(string Id, string Name, int Iterations, int Corrections, bool ContactCorrection = false, bool PositionStabilization = false,
    PhysicsSolver Solver = PhysicsSolver.Avbd, bool Sleeping = true);
internal sealed record BodyDescription(double[] Size, double Mass);
internal sealed record FrameErrors(double PositionRms, double PositionMaximum, double AngleRms, double JointError, double Penetration, double Residual, double? GroundReaction);
internal sealed record MethodSummary(string Id, string Name, int Iterations, int Corrections, bool ContactCorrection, bool PositionStabilization, double MedianMs, double P95Ms,
    double PositionRms, double PositionMaximum, double AngleRms, double JointError, double Penetration, double Residual,
    double? EndGroundReaction, FrameErrors[] Errors, double[] StepTimesMs, string Motion, int[]? LocalIterations = null, double MeanMs = 0,
    int[]? StabilizationIterations = null, int[]? TreeIterations = null, int[]? ActiveIslands = null, int[]? SleepingBodies = null,
    int[]? BodyUpdates = null, int[]? ContactSolves = null, int[]? CollisionPasses = null,
    double? PenetrationP95 = null, int? PenetrationStep = null);
internal sealed record SceneComparison(string Id, string Name, int Dimension, int OriginalScene, int Frames, int DisturbanceFrame,
    BodyDescription[] Bodies, MethodSummary[] Methods, bool? IterationAgreement, double ReferencePositionTolerance, double ReferenceAngleTolerance,
    PhysicsInput Input, JsonElement Reference, BudgetComparison Budget);
internal sealed record BudgetComparison(int BaselineIterations, int EnhancedIterations, double BaselineRatio,
    int AvbdBudgetIterations, double BudgetRatio, string Calibration);
internal sealed record ComparisonReport(string Created, string Processor, string Framework, double StepSeconds, int WarmupFrames,
    string MotionEncoding, string Timing, JsonElement Sources, SceneComparison[] Scenes, bool SleepingEnabled = true);

internal static unsafe class Research
{
    internal static readonly PhysicsSceneDescription[] Scenes = PhysicsScenes.All.ToArray();

    public static void Main(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2) options.Add(args[i].TrimStart('-'), args[i + 1]);
        if (options.GetValueOrDefault("acceptance", "false") == "true")
        {
            Environment.ExitCode = PhysicsAcceptance.Run(options);
            return;
        }
        var serialization = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        if (options.TryGetValue("serve", out string? directory))
        {
            ViewerServer.Run(Path.GetFullPath(directory), options.TryGetValue("port", out string? port) ? int.Parse(port) : 8785);
            return;
        }
        if (options.TryGetValue("comparison", out string? comparisonPath))
        {
            var saved = JsonSerializer.Deserialize<ComparisonReport>(File.ReadAllText(comparisonPath), serialization)!;
            var offlineScenes = ReadOffline(options["offline"]);
            var scenesWithReference = saved.Scenes.Select(scene =>
            {
                if (!offlineScenes.TryGetValue(scene.Id, out JsonElement independent)) return scene;
                MethodSummary method = independent.GetProperty("method").Deserialize<MethodSummary>(serialization)!;
                method = method with { MeanMs = method.StepTimesMs.Skip(saved.WarmupFrames).Average(),
                    LocalIterations = new int[method.Errors.Length], StabilizationIterations = new int[method.Errors.Length],
                    TreeIterations = new int[method.Errors.Length] };
                float[] referenceMotion = DecodeMotion(method.Motion);
                MethodSummary[] summaries = scene.Methods.Where(summary => summary.Id != "offline").Select(summary =>
                {
                    FrameErrors[] errors = CompareMotion(DecodeMotion(summary.Motion), referenceMotion, scene.Bodies, summary.Errors);
                    return summary with
                    {
                        PositionRms = Math.Sqrt(errors.Average(error => error.PositionRms * error.PositionRms)),
                        PositionMaximum = errors.Max(error => error.PositionMaximum),
                        AngleRms = Math.Sqrt(errors.Average(error => error.AngleRms * error.AngleRms)), Errors = errors
                    };
                }).Append(method).ToArray();
                return scene with { Methods = summaries, Reference = independent.GetProperty("reference").Clone() };
            }).ToArray();
            string destination = Path.GetFullPath(options.GetValueOrDefault("output", Path.GetDirectoryName(Path.GetFullPath(comparisonPath))!));
            WriteReport(saved with { Scenes = scenesWithReference }, destination, serialization);
            return;
        }
        int frames = Number("frames", 360), reference = Number("reference", 0), check = Number("check", reference * 2);
        int enhanced = Number("enhanced", PhysicsDemo.DefaultRigidIterations), baseline = Number("baseline", 8);
        bool sleeping = options.GetValueOrDefault("sleep", "true") == "true";
        int warmup = Math.Min(30, frames / 4);
        string output = Path.GetFullPath(options.GetValueOrDefault("output", "artifacts/physics-comparison"));
        var methods = new List<MethodDescription>
        {
            new("avbd", $"AVBD · {baseline} 次", baseline, 0, false, false, Sleeping: sleeping),
            new("lrc", $"AVBD + LRC · {enhanced} 轮", enhanced, 0, false, false, PhysicsSolver.LongRangeConstraints, sleeping)
        };
        if (options.GetValueOrDefault("hybrid", "false") == "true")
            methods.Add(new("hybrid", "研究混合：AVBD/LRC、关节树、解析接触", enhanced, 0, false, true, PhysicsSolver.ResearchHybrid, sleeping));
        if (reference > 0) methods.Add(new("reference", $"AVBD 迭代诊断 · {reference} 次", reference, 0, false, true));
        if (check > 0) methods.Add(new("reference-check", $"AVBD 加倍迭代诊断 · {check} 次", check, 0, false, true));
        PhysicsSceneDescription[] scenes = options.TryGetValue("scenes", out string? sceneIds) && sceneIds != "all"
            ? Scenes.Where(scene => sceneIds.Split(',').Contains(scene.Id)).ToArray() : Scenes;
        var inputs = new Dictionary<string, PhysicsInput>();
        PhysicsMaterialModel materialModel = options.GetValueOrDefault("material-model", "rigid") == "compliant"
            || options.ContainsKey("joint-stiffness") || options.ContainsKey("contact-damping")
            ? PhysicsMaterialModel.CompliantMaterials : PhysicsMaterialModel.RigidConstraints;
        foreach (PhysicsSceneDescription scene in scenes)
        {
            using var demo = new PhysicsDemo(scene.Dimension, scene.Scene, 1, PhysicsSolver.Avbd, materialModel);
            if (scene.DisturbanceFrame >= 0) demo.AddEvent(Disturbance(scene, demo.BodyCount));
            PhysicsInput input = demo.ReadInput() with
            {
                Substeps = Number("substeps", 1),
                ContactDetectionInterval = Number("contact-interval", 0),
                ContactHistoryEnabled = options.GetValueOrDefault("contact-history", "false") == "true"
            };
            if (options.TryGetValue("joint-stiffness", out string? stiffness))
            {
                double value = double.Parse(stiffness, System.Globalization.CultureInfo.InvariantCulture);
                input = input with { Materials = input.Materials! with { JointLinearStiffness = value, JointAngularStiffness = value } };
            }
            if (options.TryGetValue("contact-damping", out string? damping))
                input = input with { Materials = input.Materials! with { ContactDamping = double.Parse(damping, System.Globalization.CultureInfo.InvariantCulture) } };
            inputs.Add(scene.Id, input);
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "scene-input.json"), JsonSerializer.Serialize(new
        {
            created = DateTimeOffset.Now.ToString("O"), frames,
            scenes = scenes.Select(scene => new { scene.Id, scene.Name, scene.Dimension, frames, input = inputs[scene.Id] })
        }, serialization));
        if (options.GetValueOrDefault("input-only", "false") == "true") return;
        string metricsPath = Path.Combine(output, "metrics.jsonl");
        File.WriteAllText(metricsPath, string.Empty);
        Dictionary<string, JsonElement> offline = options.TryGetValue("offline", out string? offlinePath) ? ReadOffline(offlinePath) : [];
        var comparisons = new List<SceneComparison>();
        foreach (PhysicsSceneDescription scene in scenes)
        {
            PhysicsInput input = inputs[scene.Id];
            var runs = methods.Select(method => new Simulation(scene, method, frames, input)).ToList();
            try
            {
                Console.WriteLine($"{scene.Id}: {frames} frames, {runs[0].BodyCount} bodies, reference {reference}/{check}");
                for (int frame = 0; frame < frames; frame++)
                {
                    // Rotate timing order while retaining the same simulation frame in every method.
                    for (int index = 0; index < runs.Count; index++) runs[(index + frame) % runs.Count].Step(frame);
                }
                float[] comparisonMotion = reference > 0 ? runs[^1].Motion : runs[0].Motion;
                MethodSummary? offlineMethod = null;
                JsonElement referenceInfo;
                if (offline.TryGetValue(scene.Id, out JsonElement independent))
                {
                    offlineMethod = independent.GetProperty("method").Deserialize<MethodSummary>(serialization)!;
                    offlineMethod = offlineMethod with { MeanMs = offlineMethod.StepTimesMs.Skip(warmup).Average(),
                        LocalIterations = new int[offlineMethod.Errors.Length], StabilizationIterations = new int[offlineMethod.Errors.Length],
                        TreeIterations = new int[offlineMethod.Errors.Length] };
                    comparisonMotion = DecodeMotion(offlineMethod.Motion);
                    referenceInfo = independent.GetProperty("reference").Clone();
                }
                else referenceInfo = JsonSerializer.SerializeToElement(new
                {
                    status = reference > 0 ? "iteration-diagnostic" : "pairwise",
                    model = reference > 0 ? "高迭代 AVBD 轨迹接近程度" : "AVBD 与 LRC 轨迹差异"
                });
                var summaries = runs.Select(run => run.Summarize(comparisonMotion, warmup)).ToList();
                double smallestSize = runs[0].Descriptions.Where(body => body.Mass > 0).Min(body => body.Size.Take(scene.Dimension).Min());
                double positionTolerance = smallestSize * 0.002;
                double angleTolerance = Math.PI / 1800;
                bool? iterationAgreement = null;
                if (reference > 0 && check > 0)
                {
                    MethodSummary diagnostic = runs[2].Summarize(runs[3].Motion, warmup);
                    iterationAgreement = diagnostic.PositionMaximum <= positionTolerance && diagnostic.AngleRms <= angleTolerance;
                }
                double targetMs = summaries[1].MeanMs;
                int budgetIterations = Number("budget-iterations", baseline);
                Simulation budgetRun = RunBudget(budgetIterations);
                MethodSummary budgetSummary = budgetRun.Summarize(comparisonMotion, warmup);
                runs.Add(budgetRun); summaries.Add(budgetSummary);
                if (offlineMethod is not null) summaries.Add(offlineMethod);
                var budget = new BudgetComparison(baseline, enhanced, targetMs / summaries[0].MeanMs,
                    budgetIterations, targetMs / budgetSummary.MeanMs,
                    "one iteration count shared by every scene; measured mean complete-step ratio");
                comparisons.Add(new(scene.Id, scene.Name, scene.Dimension, scene.Scene, frames, scene.DisturbanceFrame,
                    runs[0].Descriptions, summaries.ToArray(), iterationAgreement, positionTolerance, angleTolerance, input, referenceInfo, budget));
                foreach (MethodSummary summary in summaries)
                {
                    string metric = JsonSerializer.Serialize(new { scene = scene.Id, summary.Id, summary.MeanMs, summary.MedianMs, summary.P95Ms,
                        summary.PositionRms, summary.PositionMaximum, summary.AngleRms, summary.JointError, summary.Penetration,
                        summary.PenetrationP95, summary.PenetrationStep, summary.Residual, summary.EndGroundReaction });
                    Console.WriteLine(metric);
                    File.AppendAllText(metricsPath, metric + Environment.NewLine);
                }
                Console.WriteLine($"budgetRatio={budget.BudgetRatio:G4}, baselineRatio={budget.BaselineRatio:G4}, independentReference={offlineMethod is not null}");

                Simulation RunBudget(int count)
                {
                    var method = new MethodDescription("avbd-budget", $"AVBD 预算对照 · {count} 次", count, 0, false, false, Sleeping: sleeping);
                    var run = new Simulation(scene, method, frames, input);
                    for (int frame = 0; frame < frames; frame++) run.Step(frame);
                    return run;
                }
            }
            finally { foreach (Simulation run in runs) run.Dispose(); }
        }
        using JsonDocument sources = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "References", "Sources.json")));
        var report = new ComparisonReport(DateTimeOffset.Now.ToString("O"), Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "",
            RuntimeInformation.FrameworkDescription, inputs.Values.First().StepSeconds, warmup,
            "base64 little-endian float32; frame-major, body-major; x,y,z,qx,qy,qz,qw; includes frame zero",
            "Release, tiered compilation disabled; CPU island scheduling; complete step includes collision geometry, configured AVBD primal/dual iterations, selected LRC generation/solve and reaction transfer, optional explicit stabilization, scheduling and interop; hybrid methods additionally include joint tree and analytic contact solvers; export and playback excluded",
            sources.RootElement.Clone(), comparisons.ToArray(), sleeping);
        WriteReport(report, output, serialization);

        int Number(string name, int fallback) => options.TryGetValue(name, out string? value) ? int.Parse(value) : fallback;
    }

    internal static PhysicsEvent Disturbance(PhysicsSceneDescription scene, int bodyCount)
        => new(scene.DisturbanceFrame, bodyCount - 1,
            scene.Id == "2d-stack-disturbed" ? [2, 0, 0] : [0, 2, 0]);

    private static float[] DecodeMotion(string encoded)
        => MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(encoded)).ToArray();

    private static Dictionary<string, JsonElement> ReadOffline(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("scenes").EnumerateArray()
            .ToDictionary(scene => scene.GetProperty("id").GetString()!, scene => scene.Clone());
    }

    private static void WriteReport(ComparisonReport report, string output, JsonSerializerOptions serialization)
    {
        Directory.CreateDirectory(output);
        string json = JsonSerializer.Serialize(report, serialization);
        File.WriteAllText(Path.Combine(output, "comparison.json"), json);
        string viewer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Viewer.html"));
        using var compressedReport = new MemoryStream();
        using (var gzip = new GZipStream(compressedReport, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        string replayData = JsonSerializer.Serialize(Convert.ToBase64String(compressedReport.ToArray()));
        File.WriteAllText(Path.Combine(output, "comparison.html"), viewer.Replace("/*COMPARISON_DATA*/null", replayData));
        Console.WriteLine(Path.Combine(output, "comparison.html"));
    }

    internal static FrameErrors[] CompareMotion(float[] motion, float[] referenceMotion, BodyDescription[] bodies, FrameErrors[] measured)
    {
        var errors = new FrameErrors[measured.Length];
        for (int frame = 0; frame < measured.Length; frame++)
        {
            double positionSquared = 0, positionMaximum = 0, angleSquared = 0;
            int dynamicCount = 0;
            for (int body = 0; body < bodies.Length; body++)
            {
                if (bodies[body].Mass == 0) continue;
                dynamicCount++;
                int offset = ((frame + 1) * bodies.Length + body) * 7;
                double squared = 0;
                for (int axis = 0; axis < 3; axis++) squared += Math.Pow((double)motion[offset + axis] - referenceMotion[offset + axis], 2);
                positionSquared += squared; positionMaximum = Math.Max(positionMaximum, Math.Sqrt(squared));
                double dot = 0, lengthA = 0, lengthB = 0;
                for (int axis = 3; axis < 7; axis++)
                {
                    double a = motion[offset + axis], b = referenceMotion[offset + axis];
                    dot += a * b; lengthA += a * a; lengthB += b * b;
                }
                double angle = 2 * Math.Acos(Math.Clamp(Math.Abs(dot) / Math.Sqrt(lengthA * lengthB), 0, 1));
                angleSquared += angle * angle;
            }
            errors[frame] = measured[frame] with
            {
                PositionRms = Math.Sqrt(positionSquared / dynamicCount), PositionMaximum = positionMaximum,
                AngleRms = Math.Sqrt(angleSquared / dynamicCount)
            };
        }
        return errors;
    }
}

internal sealed unsafe class Simulation : IDisposable
{
    private readonly PhysicsDemo demo;
    private readonly MethodDescription method;
    private readonly BodyFrame[] bodyFrames;
    private readonly float[] motion;
    private readonly double[] times, jointError, penetration, residual, groundReaction;
    private readonly int[] localIterations, stabilizationIterations, treeIterations, activeIslands, sleepingBodies, bodyUpdates, contactSolves, collisionPasses;
    public int BodyCount { get; }
    public BodyDescription[] Descriptions { get; }
    public float[] Motion => motion;

    public Simulation(PhysicsSceneDescription scene, MethodDescription method, int frames, PhysicsInput input)
    {
        this.method = method;
        demo = new PhysicsDemo(scene.Dimension, scene.Scene, method.Iterations, method.Solver, input)
        { PositionStabilizationEnabled = method.PositionStabilization };
        demo.SleepingEnabled = method.Sleeping;
        BodyCount = demo.BodyCount;
        bodyFrames = new BodyFrame[BodyCount];
        motion = new float[(frames + 1) * BodyCount * 7];
        times = new double[frames]; jointError = new double[frames]; penetration = new double[frames]; residual = new double[frames]; groundReaction = new double[frames];
        localIterations = new int[frames];
        stabilizationIterations = new int[frames];
        treeIterations = new int[frames];
        activeIslands = new int[frames]; sleepingBodies = new int[frames]; bodyUpdates = new int[frames];
        contactSolves = new int[frames];
        collisionPasses = new int[frames];
        ReadMotion(0);
        Descriptions = new BodyDescription[BodyCount];
        for (int i = 0; i < BodyCount; i++)
        {
            BodyFrame body = bodyFrames[i];
            Descriptions[i] = new(new[] { body.Size[0], body.Size[1], body.Size[2] }, body.Mass);
        }
    }

    public void Step(int frame)
    {
        long start = Stopwatch.GetTimestamp();
        demo.Step();
        times[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        localIterations[frame] = demo.ReadIterations();
        stabilizationIterations[frame] = demo.ReadStabilizationIterations();
        treeIterations[frame] = demo.ReadTreeIterations();
        PhysicsActivity[] activity = demo.ReadActivity();
        activeIslands[frame] = activity.Where(body => body.Island >= 0 && !body.Sleeping).Select(body => body.Island).Distinct().Count();
        sleepingBodies[frame] = activity.Count(body => body.Sleeping);
        bodyUpdates[frame] = demo.ReadBodyUpdates();
        contactSolves[frame] = demo.ReadContactSolves();
        collisionPasses[frame] = demo.ReadCollisionPasses();
        ReadMotion(frame + 1);
        jointError[frame] = demo.ReadJointError();
        residual[frame] = demo.ReadResidual();
        penetration[frame] = demo.ReadPenetration();
        groundReaction[frame] = demo.ReadGroundReaction();
    }

    private void ReadMotion(int frame)
    {
        demo.ReadFrames(bodyFrames);
        int offset = frame * BodyCount * 7;
        for (int i = 0; i < BodyCount; i++)
        {
            BodyFrame body = bodyFrames[i];
            for (int axis = 0; axis < 3; axis++) motion[offset++] = (float)body.Position[axis];
            for (int axis = 0; axis < 4; axis++) motion[offset++] = (float)body.Rotation[axis];
        }
    }

    public MethodSummary Summarize(float[] referenceMotion, int warmup)
    {
        FrameErrors[] measured = Enumerable.Range(0, times.Length).Select(frame => new FrameErrors(0, 0, 0,
            jointError[frame], penetration[frame], residual[frame], groundReaction[frame])).ToArray();
        FrameErrors[] errors = Research.CompareMotion(motion, referenceMotion, Descriptions, measured);
        double[] sorted = times.Skip(warmup).Order().ToArray();
        return new(method.Id, method.Name, method.Iterations, method.Corrections, method.ContactCorrection, method.PositionStabilization, sorted[sorted.Length / 2], sorted[(int)(sorted.Length * 0.95)],
            Math.Sqrt(errors.Average(error => error.PositionRms * error.PositionRms)), errors.Max(error => error.PositionMaximum),
            Math.Sqrt(errors.Average(error => error.AngleRms * error.AngleRms)), jointError.Max(), penetration.Max(), residual.Max(),
            groundReaction[^1], errors, times, Convert.ToBase64String(MemoryMarshal.AsBytes(motion.AsSpan())), localIterations, sorted.Average(), stabilizationIterations, treeIterations,
            activeIslands, sleepingBodies, bodyUpdates, contactSolves, collisionPasses,
            penetration.Order().ElementAt((int)(penetration.Length * .95)), Array.IndexOf(penetration, penetration.Max()) + 1);
    }

    public void Dispose()
    {
        demo.Dispose();
    }
}
