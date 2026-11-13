using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SomeEngine.ECS.Comparison;

internal static class ComparisonEntry
{
    public static int Main(string[] args)
    {
        BenchmarkCpu.BindCurrentThread();
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ComparisonWorld).TypeHandle);
        if (args[0] == "storage") return StorageCosts.Run(args);
        if (args[0] == "storage-set") return StorageCosts.RunSet(args);
        if (args[0] == "cycle-costs") return CycleCosts.Run(args);
        if (args[0] == "checkpoint-costs") return CheckpointCosts.Run(args);
        if (args[0] == "check-native-values") { NativeValueChecks.Run(); return 0; }
        if (args[0] == "check-simd") { SimdChecks.Run(args); return 0; }
        if (args[0] == "check-public-query-policy") { PublicQueryPolicyChecks.Run(); return 0; }
        if (args[0] == "check-tracked-pool-resources") return TrackedPoolResourceChecks.Run(args);
        if (args[0] == "check-tracked-pool-holding") return TrackedPoolResourceChecks.RunHolding(args);
        if (args[0] == "check-atomic-commands") { AtomicCommandChecks.Run(); return 0; }
        if (args[0] == "check-read-world") { ReadWorldChecks.Run(); return 0; }
        if (args[0] == "check-endpoint-edits") { EndpointEditChecks.Run(); CapturedTopologyChecks.Run(); return 0; }
        if (args[0] == "archive") return ArchiveComparison.Run(args);
        if (args[0] == "archive-phase") return ArchivePhaseComparison.Run(args);
        if (args[0] == "lifecycle") return LifecycleComparison.Run(args);
        if (args[0] == "tracked-jobs") return TrackedJobsComparison.Run(args);
        if (args[0] == "check-tracked-jobs") { TrackedJobsComparison.Check(args.Length > 1 ? int.Parse(args[1]) : 1024); return 0; }
        if (args[0] == "check-archive-growth") { ArchiveComparison.CheckGrowth(args.Length > 1 ? int.Parse(args[1]) : 1024); return 0; }
        if (args[0] == "check-file-store") { FileStoreChecks.Run(); return 0; }
        if (args[0] == "check-archive-file") { ArchiveComparison.CheckAll(args.Length > 1 ? int.Parse(args[1]) : 1024, formatFilter: "file"); return 0; }
        if (args[0] == "check-archive-save") { ArchiveComparison.CheckAll(args.Length > 1 ? int.Parse(args[1]) : 1024, true); return 0; }
        if (args[0] == "check-mixed-lifecycle") { MixedLifecycleComparison.Check(args.Length > 1 ? int.Parse(args[1]) : 1024); return 0; }
        if (args[0] == "check-archive") { ArchiveComparison.CheckAll(args.Length > 1 ? int.Parse(args[1]) : 1024); return 0; }
        if (args[0] == "check-archive-systems") { ArchiveSystemChecks.Run(); return 0; }
        if (args[0] == "check-generated") { GeneratedChecks.Run(); return 0; }
        if (args[0] == "check-ordered-hierarchy")
        {
            foreach (string hierarchyWorkload in new[] { "hierarchy-wide-ordered", "hierarchy-deep-ordered" })
            foreach (var library in CreateLibraries(1024, hierarchyWorkload))
            {
                using ComparisonWorld world = library.Create(); Action action = world.GetAction(hierarchyWorkload);
                action(); world.CheckResult(hierarchyWorkload); action(); world.CheckResult(hierarchyWorkload);
                world.GrowAndReclaim(action); world.CheckResult(hierarchyWorkload);
                Console.WriteLine(library.Name + ": " + hierarchyWorkload + " values, ordered children, facts and growth passed.");
            }
            return 0;
        }
        if (args[0] == "phase") return PhaseComparison.Run(args);
        if (args[0] == "check-jobs")
        {
            JobChecks.Run();
            return 0;
        }
        if (args[0] == "check-systems")
        {
            SystemChecks.Run();
            return 0;
        }
        if (args[0] == "check-random-jobs")
        {
            foreach (string mode in new[] { "execute", "parallel", "schedule", "schedule-dependent" })
                foreach (string access in new[] { "read", "write" })
                {
                    string jobWorkload = "job-" + mode + "-random-" + access;
                    foreach (var library in CreateLibraries(10_000, jobWorkload))
                    {
                        using ComparisonWorld world = library.Create();
                        world.GetAction(jobWorkload)(); world.CheckResult(jobWorkload);
                    }
                    Console.WriteLine(jobWorkload + ": all five native results and declared execution policies passed.");
                }
            return 0;
        }
        if (args[0] == "check-system-group")
        {
            SystemChecks.CheckGroup();
            return 0;
        }
        if (args[0] == "check-system-group-comparison")
        {
            foreach (var library in CreateLibraries(10_000, "job-system-group"))
            {
                using ComparisonWorld world = library.Create();
                Action update = world.GetAction("job-system-group");
                update(); world.CheckResult("job-system-group");
                update(); world.CheckResult("job-system-group");
            }
            Console.WriteLine("All five system groups: cached graph, two row stages, command playback/results, deferred hierarchy maintenance and completed system baselines passed.");
            return 0;
        }
        if (args[0] == "check-save-external")
        {
            ExternalReferenceChecks.Run();
            return 0;
        }
        if (args[0] == "check-checkpoint")
        {
            SerializationChecks.CheckMemory();
            NamedDestroyedReaderChecks.Run();
            return 0;
        }
        if (args[0] == "check-save-io")
        {
            SerializationChecks.CheckIo();
            return 0;
        }
        if (args[0] == "check-save-binary")
        {
            SerializationChecks.CheckBinary();
            return 0;
        }
        if (args[0] == "check-save-background")
        {
            SerializationChecks.CheckBackground();
            return 0;
        }
        if (args[0] == "check-copy-comparison")
        {
            foreach (string copyWorkload in new[] { "clone-set", "prefab-set" })
                foreach (var library in CreateLibraries(10_000))
                {
                    using var world = library.Create();
                    Action action = world.GetAction(copyWorkload);
                    world.GrowAndReclaim(action);
                    world.CheckResult(copyWorkload);
                }
            Console.WriteLine("All five Clone/Prefab implementations: complete internal/external references, independent containers, native values, indexes and capacity growth passed.");
            return 0;
        }
        if (args[0] == "check-copy")
        {
            CopyChecks.Run();
            return 0;
        }
        if (args[0] == "check-commands")
        {
            CommandChecks.Run();
            return 0;
        }
        if (args[0] == "check-command-comparison")
        {
            foreach (string commandWorkload in new[] { "commands-1", "commands-4" })
                foreach (var library in CreateLibraries(10_000))
                {
                    using var world = library.Create();
                    Action action = world.GetAction(commandWorkload);
                    world.GrowAndReclaim(action);
                    world.CheckResult(commandWorkload);
                    if (!world.CommandScenario!.CheckProtocol()) throw new InvalidOperationException($"{library.Name}: {commandWorkload} result lifetime or prefix check failed.");
                }
            Console.WriteLine("All five command implementations: one/four producers, 1,000-command FIFO batches, capacity growth, failure prefixes and retained result handles passed.");
            return 0;
        }
        if (args[0] == "check-relation-comparison")
        {
            string[] modes = ["directed-unique-pair", "directed-unique-source", "directed-unique-target", "directed-one-to-one", "undirected-unique-pair-ordered", "undirected-one-to-one-ordered"];
            foreach (string mode in modes)
                foreach (var library in CreateLibraries(10_000))
                {
                    using var world = library.Create();
                    world.GetAction("relation-" + mode);
                    if (!world.RelationScenario!.CheckClaims()) throw new InvalidOperationException($"{library.Name}: {mode} final claim check failed.");
                }
            foreach (string mode in new[] { "directed-one-to-one-ordered", "undirected-one-to-one-ordered" })
                foreach (var library in CreateLibraries(10_000))
                {
                    using var world = library.Create(); world.GetAction("relation-" + mode);
                    if (!world.RelationScenario!.CheckLifetimes()) throw new InvalidOperationException($"{library.Name}: {mode} deferred lifetime check failed.");
                    world.AddRemove();
                }
            Console.WriteLine("All five native relation implementations: final claim rejection, legal exchange, one write version and retained topology passed.");
            return 0;
        }
        if (args[0] == "check-relations")
        {
            RelationChecks.Run();
            return 0;
        }
        if (args[0] == "check-hierarchy")
        {
            HierarchyChecks.Run();
            return 0;
        }
        if (args[0] == "check-query-binding")
        {
            QueryBindingChecks.Run();
            return 0;
        }
        if (args[0] == "check-lifecycle")
        {
            LifecycleChecks.Run();
            return 0;
        }
        if (args[0] == "check-query-conditions")
        {
            QueryConditionChecks.Run();
            return 0;
        }
        if (args[0] == "check-chunks")
        {
            CoreChecks.Run(32);
            GroupChecks.Run(4);
            ChunkChecks.Run();
            return 0;
        }
        if (args[0] == "check-growth")
        {
            string[] workloads = args.Length > 1 ? args[1..] : ["enableable", "changes-25", "buffer-4", "buffer-32", "resources",
                "shared-change-16", "shared-change-unique", "index-change", "sparse-1", "sparse-25", "group-change"];
            foreach (string growthWorkload in workloads)
            {
                foreach (var library in CreateLibraries(10_000))
                {
                    using var world = library.Create();
                    Action action = world.GetAction(growthWorkload);
                    world.GrowAndReclaim(action);
                    world.CheckResult(growthWorkload);
                }
                Console.WriteLine($"{growthWorkload}: growth behavior passed.");
            }
            return 0;
        }
        if (args[0] == "check-groups")
        {
            GroupChecks.Run();
            return 0;
        }
        if (args[0] == "check-index")
        {
            IndexChecks.Run();
            return 0;
        }
        if (args[0] == "check-shared")
        {
            SharedChecks.Run();
            return 0;
        }
        if (args[0] == "check-containers")
        {
            ContainerChecks.Run();
            return 0;
        }
        if (args[0] == "check-sparse-query")
        {
            SparseQueryChecks.Run();
            return 0;
        }
        if (args[0] == "check-changes")
        {
            ChangeChecks.Run();
            return 0;
        }
        if (args[0] == "check-enabled")
        {
            EnabledChecks.Run();
            return 0;
        }
        if (args[0] == "check-presence")
        {
            PresenceChecks.Run();
            return 0;
        }
        if (args[0] == "check-access")
        {
            AccessChecks.Run();
            return 0;
        }
        if (args[0] is "once" or "once-growth" or "once-cycle")
        {
            foreach (var library in CreateLibraries(int.Parse(args[2]), args[1]))
            {
                Console.WriteLine($"{library.Name}: public {args[0]} {args[1]} N={args[2]} began.");
                using var world = library.Create();
                Action onceAction = world.GetAction(args[1]);
                int queryJobRuns = 1;
                if (args[0] == "once-growth") world.GrowAndReclaim(onceAction);
                else if (args[0] == "once-cycle")
                {
                    int operations = args.Length > 3 ? int.Parse(args[3]) : 2;
                    queryJobRuns = operations;
                    for (int operation = 1; operation <= operations; operation++)
                    {
                        onceAction();
                        if (operation % 10 == 0 || operation == operations / 2) world.AddRemove();
                    }
                }
                else onceAction();
                world.CheckResult(args[1], queryJobRuns);
            }
            Console.WriteLine("Public workload results: passed.");
            return 0;
        }
        if (args[0] == "check")
        {
            CoreChecks.Run();
            return 0;
        }
        string workload = args[0];
        int count = int.Parse(args[1]);
        string output = Path.GetFullPath(args[2]);
        var libraries = CreateLibraries(count, workload);
        var repetitions = new int[libraries.Length];
        var samples = new List<Measurement>[libraries.Length];
        int? endpointCount = null, edgeCount = null;
        for (int library = 0; library < libraries.Length; library++)
        {
            using ComparisonWorld world = libraries[library].Create();
            Action action = world.GetAction(workload);
            if (library == 0)
            {
                endpointCount = world.RelationScenario?.EndpointCount;
                edgeCount = world.RelationScenario?.EdgeCount;
            }
            action();
            world.CheckResult(workload);
            samples[library] = [];
            repetitions[library] = Calibrate(action);
            Console.WriteLine($"{libraries[library].Name}: result checked, {repetitions[library]} repetitions/sample");
        }
        // Rotate the first library each round so every library occupies every position.
        for (int round = 0; round < 10; round++)
        {
            for (int offset = 0; offset < libraries.Length; offset++)
            {
                int library = (round + offset) % libraries.Length;
                using ComparisonWorld world = libraries[library].Create();
                Action action = world.GetAction(workload);
                action(); world.CheckResult(workload); action();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                int repeats = repetitions[library];
                long beforeBytes = GC.GetTotalAllocatedBytes(true);
                long start = Stopwatch.GetTimestamp();
                for (int repeat = 0; repeat < repeats; repeat++) action();
                double nanoseconds = Stopwatch.GetElapsedTime(start).TotalNanoseconds / repeats;
                double bytes = (double)(GC.GetTotalAllocatedBytes(true) - beforeBytes) / repeats;
                samples[library].Add(new(nanoseconds, bytes));
                Console.WriteLine(FormattableString.Invariant(
                    $"{libraries[library].Name} sample {round + 1}: {nanoseconds:F2} ns, {bytes:F2} B"));
            }
        }
            var report = new
            {
                Workload = workload,
                Entities = count,
                EndpointCount = endpointCount,
                EdgeCount = edgeCount,
                Samples = 10,
                TargetSampleMilliseconds = 100,
                ActionThreadLifetime = BufferActionThread.UsesActionThread(workload) ? "One action" : "Measurement",
                TieredCompilation = false,
                ServerGC = GCSettings.IsServerGC,
                Framework = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                ParticipantProcessors = BenchmarkCpu.ParticipantProcessors,
                TimingProcessor = Thread.GetCurrentProcessorId(),
                Results = libraries.Select((library, index) => new
                {
                    Library = library.Name,
                    Storage = QueryStorage(workload, library.Name),
                    Repetitions = repetitions[index],
                    MedianNanoseconds = Median(samples[index].Select(sample => sample.Nanoseconds)),
                    MinNanoseconds = samples[index].Min(sample => sample.Nanoseconds),
                    MaxNanoseconds = samples[index].Max(sample => sample.Nanoseconds),
                    AllocatedBytes = Median(samples[index].Select(sample => sample.AllocatedBytes)),
                    Measurements = samples[index],
                }),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(output, json + Environment.NewLine);
            Console.WriteLine(json);
            return 0;
    }

    internal static (string Name, Func<ComparisonWorld> Create)[] CreateLibraries(int count, string? workload = null)
    {
        BenchmarkCpu.ConfigureNative(workload);
        PositionInputs.Get(count);
        return [
        ("SomeEngine", () => new SomeEngineWorld(count, workload is "query-append-fixed" or "query-bind-fixed" ? 256 : 0,
            workload?.StartsWith("job-") == true && workload != "job-native-parallel", workload == "enableable",
            workload is "create-destroy-three" or "spawn-batch" ? ComparisonWorld.Changes : 0, workload)),
        ("EasyEcs", () => new EasyEcsWorld(count, workload == "job-native-parallel", workload == "enableable", workload)),
        ("Friflo", () => new FrifloWorld(count, workload == "enableable", workload)),
        ("Arch", () => new ArchWorld(count, workload == "enableable", workload)),
        ("DefaultEcs", () => new DefaultEcsWorld(count)),
        ];
    }

    internal static string? QueryStorage(string workload, string library)
    {
        if (!workload.StartsWith("query-")) return null;
        if (!workload.EndsWith("-fixed")) return library switch
        {
            "Arch" => "Native fixed chunks",
            "DefaultEcs" => "Native component pools",
            _ => "Growing archetype columns",
        };
        return library switch
        {
            "SomeEngine" => "Fixed storage chunks, 256 rows",
            "Arch" => "Native fixed chunks, 256-row views",
            "DefaultEcs" => "Native component pools, 256-row views",
            _ => "Growing native archetype columns, 256-row views",
        };
    }

    private static int Calibrate(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        int repeats = 0;
        do
        {
            action();
            repeats++;
        } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 300);
        return Math.Max(1, (int)(repeats * 100 / Stopwatch.GetElapsedTime(start).TotalMilliseconds));
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return (sorted[4] + sorted[5]) / 2;
    }

    private sealed record Measurement(double Nanoseconds, double AllocatedBytes);
}
