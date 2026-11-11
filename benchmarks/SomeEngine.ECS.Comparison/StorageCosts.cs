using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.ECS.Comparison;

internal static class StorageCosts
{
    private readonly record struct ProcessMemory(long PrivateBytes, long WorkingSetBytes, long PeakWorkingSetBytes);
    private readonly record struct MemoryObservation(long ManagedRetainedBytes, long ManagedTotalAllocatedBytes,
        long ObservationAllocatedBytes, ProcessMemory Process);
    private readonly record struct ActionResult(long? OutputBytes, long? OutputCapacityBytes, long ValidationAllocatedBytes,
        CompactArchiveOutput.Costs? CompactOutput = null);

    internal static int RunSet(string[] args)
    {
        string workload = args[1];
        int count = int.Parse(args[2]);
        string output = Path.GetFullPath(args[3]);
        string phase = args.Length > 4 ? args[4] : "first";
        string directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        string[] libraries = ["SomeEngine", "EasyEcs", "Friflo", "Arch", "DefaultEcs"];
        var results = new JsonElement[libraries.Length];
        var childReports = new string[libraries.Length];
        string assembly = typeof(StorageCosts).Assembly.Location;
        for (int i = 0; i < libraries.Length; i++)
        {
            string childOutput = Path.Combine(directory, Path.GetFileNameWithoutExtension(output) + "." + libraries[i] + ".json");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (string argument in new[] { assembly, "storage", workload, libraries[i],
                count.ToString(System.Globalization.CultureInfo.InvariantCulture), childOutput, phase })
                start.ArgumentList.Add(argument);
            using Process child = Process.Start(start)!;
            Task<string> standardOutput = child.StandardOutput.ReadToEndAsync();
            Task<string> standardError = child.StandardError.ReadToEndAsync();
            child.WaitForExit();
            string stdout = standardOutput.GetAwaiter().GetResult();
            string stderr = standardError.GetAwaiter().GetResult();
            if (child.ExitCode != 0)
                throw new InvalidOperationException($"{libraries[i]} storage process exited with {child.ExitCode}.{Environment.NewLine}{stderr}{stdout}");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(childOutput));
            results[i] = document.RootElement.Clone();
            childReports[i] = childOutput;
        }
        var report = new
        {
            Measurement = "StorageCostsSet", Host = Environment.MachineName, CpuModel = CpuModel(),
            CoordinatorProcessId = Environment.ProcessId, Phase = phase, Workload = workload, Entities = count, Units = "bytes",
            Coordination = "Five sequential independent .NET child processes on this host, each measuring one library and one benchmark instance, including its capability state.",
            MetricsScope = "Every result retains its child-process counters and memory metrics; the outer process coordinates execution.",
            ChildReports = childReports, Results = results,
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json + Environment.NewLine);
        Console.WriteLine(json);
        return 0;
    }

    internal static int Run(string[] args)
    {
        string workload = args[1], requestedLibrary = args[2];
        int count = int.Parse(args[3]);
        string output = Path.GetFullPath(args[4]);
        string phase = args.Length > 5 ? args[5] : "first";
        bool compact = workload is "save-compact-preserve-codec" or "save-compact-preserve-raw"
            or "save-compact-rebuild-codec" or "save-compact-rebuild-raw";
        bool archive = compact || workload is "save-preserve-codec" or "save-preserve-raw" or "save-rebuild-codec" or "save-rebuild-raw";
        bool preserve = workload.Contains("-preserve-", StringComparison.Ordinal), raw = workload.EndsWith("-raw");
        (string Name, Func<ComparisonWorld> Create)[]? libraries = null;
        ArchiveComparison.Library[]? archiveLibraries = null;
        Func<ComparisonWorld>? create = null;
        Func<FullArchiveWorld>? createArchive = null;
        string libraryName;
        if (archive)
        {
            archiveLibraries = ArchiveComparison.Libraries(count, raw);
            var library = archiveLibraries.Single(item => string.Equals(item.Name, requestedLibrary, StringComparison.OrdinalIgnoreCase));
            libraryName = library.Name;
            createArchive = library.Create;
        }
        else
        {
            libraries = ComparisonEntry.CreateLibraries(count, workload);
            var library = libraries.Single(item => string.Equals(item.Name, requestedLibrary, StringComparison.OrdinalIgnoreCase));
            libraryName = library.Name;
            create = library.Create;
        }
        string? cpu = CpuModel();
        string host = Environment.MachineName;
        var snapshots = new MemoryObservation[5];
        ReadProcessMemory();
        snapshots[0] = Capture();
        ActionResult actionResult = archive
            ? MeasureArchive(createArchive!, preserve, compact, libraryName, snapshots)
            : MeasureWorld(create!, workload, phase, snapshots);
        snapshots[4] = Capture();
        GC.KeepAlive(libraries);
        GC.KeepAlive(archiveLibraries);

        string[] stages = ["WorldCreated", "CapabilityReady", "ActionCompleted", "Released"];
        string[] completions =
        [
            "The selected library constructor returned.",
            archive ? compact ? "ArchiveAction(save-compact) returned the ready action."
                : "ArchiveAction(save) returned the ready action."
                : phase == "growth" ? "GetAction and the same ready action used by PhaseComparison completed."
                : "GetAction returned the prepared action.",
            archive ? "ArchiveAction.Run returned; payload bytes and this snapshot precede Verify and World.Check."
                : phase == "growth" ? "GrowAndReclaim returned; CheckResult follows this snapshot."
                : phase == "cycle" ? "100 actions and ten AddRemove calls returned; CheckResult follows this snapshot."
                : "One complete action returned; CheckResult follows this snapshot.",
            "Dispose returned and the NoInlining world/action method returned before collection.",
        ];
        var report = new
        {
            Measurement = "StorageCosts",
            Host = host, CpuModel = cpu, OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription, ServerGC = GCSettings.IsServerGC,
            ProcessId = Environment.ProcessId, ProcessorCount = Environment.ProcessorCount,
            ParticipantProcessors = BenchmarkCpu.ParticipantProcessors,
            Phase = phase, Workload = workload, Library = libraryName, Entities = count,
            Worlds = !archive && workload == "prefab-set" && libraryName == "SomeEngine" ? 2 : 1,
            CompleteActions = !archive && phase == "cycle" ? 100 : 1,
            WorldScope = "One benchmark instance, including capability state. SomeEngine prefab also holds one template World; native prefab stores are included in each library's measured bytes.",
            Progress = archive ? preserve ? "preserve" : "rebuild" : null,
            Encoding = archive ? raw ? "raw" : "codec" : null,
            OutputConfiguration = archive ? compact ? "Compact 64 KiB segments and exact continuous result" : "ArrayBufferWriter" : null,
            Units = "bytes",
            Storage = archive ? "Full archive values and progress" : ComparisonEntry.QueryStorage(workload, libraryName),
            ActionThreadLifetime = BufferActionThread.UsesActionThread(workload) ? "One action" : "Measurement",
            InputPreparation = archive
                ? "ArchiveComparison.Libraries and the save parameters are selected before the baseline."
                : "CreateLibraries prepares the existing shared input arrays before the baseline.",
            Collection = "At every snapshot: forced blocking compacting full GC, pending finalizers, then the same full GC.",
            ManagedScope = "Whole-process managed heap retained after collection; deltas include library and runtime caches.",
            AllocationScope = "GC.GetTotalAllocatedBytes(true), across all threads. Raw deltas include runtime and prior metric sampling.",
            ProcessScope = "Whole-process private, resident and peak resident memory, including runtime and native allocations.",
            ObservationAllocation = "Each snapshot reports allocations used by its process-metric sampling after reading managed counters.",
            ValidationAllocatedBytes = actionResult.ValidationAllocatedBytes,
            OutputBytes = actionResult.OutputBytes,
            OutputCapacityBytes = actionResult.OutputCapacityBytes,
            OutputUnusedCapacityBytes = actionResult.OutputCapacityBytes - actionResult.OutputBytes,
            CompactOutput = actionResult.CompactOutput,
            CompactOutputAccounting = compact
                ? "Segment capacity includes the reused result and new segments. Array counters sum allocated payload bytes; managed readings include array headers. The retained segment directory is reported separately. Complete runs inside ArchiveAction.Run."
                : null,
            Baseline = snapshots[0],
            Stages = stages.Select((stage, index) =>
            {
                MemoryObservation current = snapshots[index + 1], previous = snapshots[index], baseline = snapshots[0];
                return new
                {
                    Stage = stage, Completion = completions[index],
                    Managed = new
                    {
                        RetainedBytes = current.ManagedRetainedBytes,
                        RetainedDeltaFromBaselineBytes = current.ManagedRetainedBytes - baseline.ManagedRetainedBytes,
                        RetainedDeltaFromPreviousBytes = current.ManagedRetainedBytes - previous.ManagedRetainedBytes,
                        TotalAllocatedBytes = current.ManagedTotalAllocatedBytes,
                        AllocatedDeltaFromBaselineBytes = current.ManagedTotalAllocatedBytes - baseline.ManagedTotalAllocatedBytes,
                        AllocatedDeltaFromPreviousBytes = current.ManagedTotalAllocatedBytes - previous.ManagedTotalAllocatedBytes,
                        current.ObservationAllocatedBytes,
                    },
                    Process = new
                    {
                        current.Process.PrivateBytes, current.Process.WorkingSetBytes, current.Process.PeakWorkingSetBytes,
                        PrivateDeltaFromBaselineBytes = current.Process.PrivateBytes - baseline.Process.PrivateBytes,
                        WorkingSetDeltaFromBaselineBytes = current.Process.WorkingSetBytes - baseline.Process.WorkingSetBytes,
                        PeakWorkingSetDeltaFromBaselineBytes = current.Process.PeakWorkingSetBytes - baseline.Process.PeakWorkingSetBytes,
                    },
                };
            }).ToArray(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        File.WriteAllText(output, json + Environment.NewLine);
        Console.WriteLine(json);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ActionResult MeasureWorld(Func<ComparisonWorld> create, string workload, string phase, MemoryObservation[] snapshots)
    {
        ComparisonWorld world = create();
        try
        {
            snapshots[1] = Capture();
            GC.KeepAlive(world);
            Action action = world.GetAction(workload);
            if (phase == "growth" && !workload.StartsWith("query-")) action();
            snapshots[2] = Capture();
            GC.KeepAlive(action); GC.KeepAlive(world);
            if (phase == "growth") world.GrowAndReclaim(action);
            else if (phase == "cycle")
                for (int operation = 1; operation <= 100; operation++)
                {
                    action();
                    if (operation % 10 == 0) world.AddRemove();
                }
            else action();
            snapshots[3] = Capture();
            GC.KeepAlive(action); GC.KeepAlive(world);
            long beforeValidation = GC.GetTotalAllocatedBytes(true);
            world.CheckResult(workload, phase == "cycle" ? 100 : 1);
            long validation = GC.GetTotalAllocatedBytes(true) - beforeValidation;
            return new(null, null, validation);
        }
        finally { world.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ActionResult MeasureArchive(Func<FullArchiveWorld> create, bool preserve, bool compact, string library, MemoryObservation[] snapshots)
    {
        FullArchiveWorld world = create();
        try
        {
            snapshots[1] = Capture();
            GC.KeepAlive(world);
            using var action = new ArchiveComparison.ArchiveAction(world, compact ? "save-compact" : "save", preserve, library);
            snapshots[2] = Capture();
            GC.KeepAlive(action); GC.KeepAlive(world);
            action.Run();
            long outputBytes = action.OutputBytes;
            long? outputCapacityBytes = action.OutputCapacityBytes;
            CompactArchiveOutput.Costs? compactOutput = action.CompactOutputCosts;
            snapshots[3] = Capture();
            GC.KeepAlive(action); GC.KeepAlive(world);
            long beforeValidation = GC.GetTotalAllocatedBytes(true);
            action.Verify();
            world.Check(preserve);
            long validation = GC.GetTotalAllocatedBytes(true) - beforeValidation;
            return new(outputBytes, outputCapacityBytes, validation, compactOutput);
        }
        finally { world.Dispose(); }
    }

    private static MemoryObservation Capture()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long retained = GC.GetTotalMemory(false);
        long allocated = GC.GetTotalAllocatedBytes(true);
        ProcessMemory process = ReadProcessMemory();
        long observation = GC.GetTotalAllocatedBytes(true) - allocated;
        return new(retained, allocated, observation, process);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ProcessMemory ReadProcessMemory()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new(process.PrivateMemorySize64, process.WorkingSet64, process.PeakWorkingSet64);
    }

    private static string? CpuModel()
        => OperatingSystem.IsLinux()
            ? File.ReadLines("/proc/cpuinfo").First(line => line.StartsWith("model name")).Split(':', 2)[1].Trim()
            : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
}
