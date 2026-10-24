using System.Globalization;

namespace SomeEngine.Graphics.Benchmarks;

public sealed class BenchmarkUsageException(string message) : Exception(message);

public enum GraphicsBackendKind : byte
{
    Direct3D12,
    Vulkan,
}

public sealed record BenchmarkOptions(
    BenchmarkCommand Command,
    BenchmarkProfile Profile,
    string OutputPath,
    AdapterId AdapterId,
    bool AdapterSpecified,
    ReceiverVariant? Variant,
    int ProcessIndex,
    int WarmupFrames,
    int MeasuredFrames,
    int DrawCount,
    int BarrierCount,
    string ShaderDirectory,
    string? NativeRunnerPath,
    string? ManagedRunnerPath,
    string? InputPath,
    string? ResumeDirectory,
    bool DefaultDirectCalls,
    ReceiverVariant[] Variants,
    GraphicsWorkload[] Workloads,
    int[] GraphicsCpuResourceCounts,
    GraphicsBackendKind GraphicsBackend,
    int ProcessCount)
{
    internal const string Usage = """
        Usage:
          Controllers accept --processes <count> (default 1); warp and diagnose accept --variants <name,...>.
          SomeEngine.Graphics.Benchmarks warp [--output <report.json>] [--native-runner <exe>]
          SomeEngine.Graphics.Benchmarks probe --adapter <low>:<high> [--workloads <name,...>] [--variants <interface-receiver,direct-silk,direct-silk-default>] [--output <report.json>] [--resume <raw-directory>]
          SomeEngine.Graphics.Benchmarks diagnose --adapter <low>:<high> [--direct-mode <optimized|default>] [--output <report.json>] [--native-runner <exe>] [--managed-runner <exe>] [--resume <raw-directory>]
          SomeEngine.Graphics.Benchmarks evaluate --input <report.json>
          SomeEngine.Graphics.Benchmarks graph-cpu --backend <d3d12|vulkan> --adapter <low>:<high> [--resource-counts <count,...>] [--output <report.json>] [--warmup <minimum-frames>] [--samples <frames>]
          SomeEngine.Graphics.Benchmarks worker --profile <warp|probe|diagnose|representative> --variant <interface-receiver|direct-silk|direct-silk-default> --adapter <low>:<high> --process-index <n> --shader-dir <path> --output <path> [--direct-mode <optimized|default>] [internal count options]
        """;

    public static BenchmarkOptions Parse(string[] args)
    {
        BenchmarkCommand command = ParseCommand(args.Length == 0 ? "warp" : args[0]);
        int start = args.Length == 0 ? 0 : 1;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = start; index < args.Length; index += 2)
        {
            string key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new BenchmarkUsageException($"Option '{key}' requires one value.");
            if (!values.TryAdd(key[2..], args[index + 1]))
                throw new BenchmarkUsageException($"Option '{key}' was supplied more than once.");
        }

        string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        string output = FullPath(
            Get(values, "output") ?? Path.Combine(
                repositoryRoot,
                "artifacts",
                "graphics-benchmarks",
                command switch
                {
                    BenchmarkCommand.Diagnose => "fast-diagnostic.json",
                    BenchmarkCommand.Probe => "developer-probe.json",
                    BenchmarkCommand.GraphCpu => "rendergraph-cpu-development.json",
                    _ => "warp-acceptance.json",
                }));
        string shaderDirectory = FullPath(
            Get(values, "shader-dir") ?? Path.Combine(
                Path.GetDirectoryName(output)!,
                "shaders"));
        string? adapterText = Get(values, "adapter");
        AdapterId adapter = adapterText is null ? default : ParseAdapter(adapterText);
        ReceiverVariant? variant = Get(values, "variant") is string variantText
            ? ParseVariant(variantText)
            : null;
        BenchmarkProfile profile = command switch
        {
            BenchmarkCommand.Diagnose => BenchmarkProfile.FastDiagnostic,
            BenchmarkCommand.Warp => BenchmarkProfile.WarpFunctional,
            BenchmarkCommand.Worker => ParseProfile(
                Get(values, "profile") ??
                throw new BenchmarkUsageException("worker requires --profile.")),
            BenchmarkCommand.Probe => BenchmarkProfile.DeveloperProbe,
            BenchmarkCommand.GraphCpu => BenchmarkProfile.GraphicsCpuDevelopment,
            _ => BenchmarkProfile.WarpFunctional,
        };

        int warmup = ParsePositive(values, "warmup", profile switch
        {
            BenchmarkProfile.WarpFunctional => BenchmarkDefaults.WarpWarmupFrames,
            BenchmarkProfile.FastDiagnostic => BenchmarkDefaults.DiagnosticWarmupFrames,
            BenchmarkProfile.DeveloperProbe => BenchmarkDefaults.ProbeWarmupFrames,
            BenchmarkProfile.RepresentativeCpuFrame => BenchmarkDefaults.RepresentativeWarmupFrames,
            BenchmarkProfile.GraphicsCpuDevelopment => BenchmarkDefaults.GraphicsCpuMinimumWarmupFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int measured = ParsePositive(values, "samples", profile switch
        {
            BenchmarkProfile.WarpFunctional => BenchmarkDefaults.WarpMeasuredFrames,
            BenchmarkProfile.FastDiagnostic => BenchmarkDefaults.DiagnosticMeasuredFrames,
            BenchmarkProfile.DeveloperProbe => BenchmarkDefaults.ProbeMeasuredFrames,
            BenchmarkProfile.RepresentativeCpuFrame => BenchmarkDefaults.RepresentativeMeasuredFrames,
            BenchmarkProfile.GraphicsCpuDevelopment => BenchmarkDefaults.GraphicsCpuMeasuredFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int draws = ParsePositive(values, "draws", profile switch
        {
            BenchmarkProfile.WarpFunctional => BenchmarkDefaults.WarpDrawCount,
            BenchmarkProfile.FastDiagnostic => BenchmarkDefaults.DiagnosticDrawCount,
            BenchmarkProfile.DeveloperProbe => BenchmarkDefaults.ProbeDrawCount,
            BenchmarkProfile.RepresentativeCpuFrame => RepresentativeFrameProfile.DrawCount,
            BenchmarkProfile.GraphicsCpuDevelopment => RepresentativeFrameProfile.DrawCount,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int barriers = profile == BenchmarkProfile.FastDiagnostic
            ? ParseNonNegative(values, "barriers", BenchmarkDefaults.DiagnosticBarrierCount)
            : ParsePositive(
                values,
                "barriers",
                profile == BenchmarkProfile.DeveloperProbe
                        ? BenchmarkDefaults.ProbeBarrierCount
                        : profile == BenchmarkProfile.RepresentativeCpuFrame
                            ? RepresentativeFrameProfile.BarrierCount
                            : profile == BenchmarkProfile.GraphicsCpuDevelopment
                                ? RepresentativeFrameProfile.BarrierCount
                            : BenchmarkDefaults.WarpBarrierCount);
        int processIndex = ParseNonNegative(values, "process-index", 0);
        string? directMode = Get(values, "direct-mode");
        bool defaultDirectCalls = Normalize(directMode ?? "optimized") switch
        {
            "optimized" => false,
            "default" => true,
            _ => throw new BenchmarkUsageException(
                "--direct-mode must be either 'optimized' or 'default'."),
        };
        GraphicsBackendKind graphicsBackend = Normalize(Get(values, "backend") ?? "d3d12") switch
        {
            "d3d12" or "direct3d12" => GraphicsBackendKind.Direct3D12,
            "vulkan" => GraphicsBackendKind.Vulkan,
            _ => throw new BenchmarkUsageException("--backend must be either 'd3d12' or 'vulkan'."),
        };

        ValidateKnown(values);
        if (command == BenchmarkCommand.Worker && (variant is null || adapterText is null))
            throw new BenchmarkUsageException("worker requires --variant and --adapter.");
        if (command is BenchmarkCommand.Diagnose or BenchmarkCommand.Probe or BenchmarkCommand.GraphCpu && adapterText is null)
            throw new BenchmarkUsageException($"{Normalize(args[0])} requires an explicit hardware --adapter LUID.");
        if (command == BenchmarkCommand.Evaluate && Get(values, "input") is null)
            throw new BenchmarkUsageException("evaluate requires --input.");
        if (Get(values, "resume") is not null &&
            command is not BenchmarkCommand.Diagnose and not BenchmarkCommand.Probe)
        {
            throw new BenchmarkUsageException("--resume is valid only with probe or diagnose.");
        }
        if (Get(values, "managed-runner") is not null &&
            command is not BenchmarkCommand.Diagnose)
        {
            throw new BenchmarkUsageException("--managed-runner is valid only with diagnose.");
        }
        if (directMode is not null &&
            command is not BenchmarkCommand.Diagnose and not BenchmarkCommand.Worker)
        {
            throw new BenchmarkUsageException(
                "--direct-mode is valid only with diagnose or its managed worker.");
        }
        if (command == BenchmarkCommand.Worker &&
            directMode is not null &&
            variant != ReceiverVariant.DirectSilk)
        {
            throw new BenchmarkUsageException(
                "--direct-mode is valid only for the direct-silk worker.");
        }
        if (command == BenchmarkCommand.Warp && adapterText is not null)
            throw new BenchmarkUsageException("warp selects the D3D12 WARP adapter automatically; --adapter is not valid.");
        if (Get(values, "backend") is not null && command != BenchmarkCommand.GraphCpu)
            throw new BenchmarkUsageException("--backend is valid only with graph-cpu.");
        return new BenchmarkOptions(
            command,
            profile,
            output,
            adapter,
            adapterText is not null,
            variant,
            processIndex,
            warmup,
            measured,
            draws,
            barriers,
            shaderDirectory,
            Get(values, "native-runner") is string native ? FullPath(native) : null,
            Get(values, "managed-runner") is string managed ? FullPath(managed) : null,
            Get(values, "input") is string input ? FullPath(input) : null,
            Get(values, "resume") is string resume ? FullPath(resume) : null,
            defaultDirectCalls,
            ParseVariants(Get(values, "variants"), command, Get(values, "native-runner") is not null),
            ParseWorkloads(Get(values, "workloads"), command),
            ParseGraphicsCpuResourceCounts(Get(values, "resource-counts"), command),
            graphicsBackend,
            ParsePositive(values, "processes", 1));
    }

    private static BenchmarkCommand ParseCommand(string value) => Normalize(value) switch
    {
        "warp" => BenchmarkCommand.Warp,
        "diagnose" => BenchmarkCommand.Diagnose,
        "worker" => BenchmarkCommand.Worker,
        "evaluate" => BenchmarkCommand.Evaluate,
        "probe" => BenchmarkCommand.Probe,
        "graph-cpu" => BenchmarkCommand.GraphCpu,
        _ => throw new BenchmarkUsageException($"Unknown command '{value}'."),
    };

    private static BenchmarkProfile ParseProfile(string value) => Normalize(value) switch
    {
        "warp" => BenchmarkProfile.WarpFunctional,
        "diagnose" => BenchmarkProfile.FastDiagnostic,
        "probe" => BenchmarkProfile.DeveloperProbe,
        "representative" => BenchmarkProfile.RepresentativeCpuFrame,
        "graph-cpu" => BenchmarkProfile.GraphicsCpuDevelopment,
        _ => throw new BenchmarkUsageException($"Unknown worker profile '{value}'."),
    };

    private static ReceiverVariant ParseVariant(string value) => Normalize(value) switch
    {
        "interface-receiver" => ReceiverVariant.InterfaceReceiver,
        "direct-silk" => ReceiverVariant.DirectSilk,
        "direct-silk-default" => ReceiverVariant.DirectSilkDefault,
        "native-cpp" => ReceiverVariant.NativeCpp,
        _ => throw new BenchmarkUsageException($"Unknown receiver variant '{value}'."),
    };

    private static ReceiverVariant[] ParseVariants(string? value, BenchmarkCommand command, bool hasNativeRunner)
    {
        ReceiverVariant[] result = value is null
            ? (command == BenchmarkCommand.Probe ? BenchmarkDefaults.ProbeVariants :
                hasNativeRunner ? BenchmarkDefaults.Variants : [ReceiverVariant.InterfaceReceiver, ReceiverVariant.DirectSilk])
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseVariant).Distinct().ToArray();
        if (command is not BenchmarkCommand.Probe and not BenchmarkCommand.Warp and not BenchmarkCommand.Diagnose && value is not null)
            throw new BenchmarkUsageException("--variants is valid with warp, probe, or diagnose.");
        if (command == BenchmarkCommand.Probe && (result.Length == 0 || result.Contains(ReceiverVariant.NativeCpp)))
            throw new BenchmarkUsageException("probe requires at least one managed variant; native-cpp does not support custom workload selection.");
        if (result.Length == 0)
            throw new BenchmarkUsageException("Select at least one variant.");
        if (result.Contains(ReceiverVariant.NativeCpp) && !hasNativeRunner)
            throw new BenchmarkUsageException("The native-cpp variant requires --native-runner.");
        return result;
    }

    private static GraphicsWorkload[] ParseWorkloads(string? value, BenchmarkCommand command)
    {
        GraphicsWorkload[] result = value is null
            ? (command == BenchmarkCommand.Probe ? BenchmarkDefaults.ProbeWorkloads : [])
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseWorkload).Distinct().ToArray();
        if (command is not BenchmarkCommand.Probe and not BenchmarkCommand.Worker && value is not null)
            throw new BenchmarkUsageException("--workloads is valid only with probe or worker.");
        if (command == BenchmarkCommand.Probe && result.Length == 0)
            throw new BenchmarkUsageException("probe requires at least one workload.");
        return result;
    }

    private static GraphicsWorkload ParseWorkload(string value) => Normalize(value) switch
    {
        "empty-submit" => GraphicsWorkload.EmptySubmit,
        "persistent-draw" or "persistent-draw10000" => GraphicsWorkload.PersistentDraw10000,
        "transient-draw" or "transient-draw10000" => GraphicsWorkload.TransientDraw10000,
        "state-suppression" or "state-suppression10000" => GraphicsWorkload.StateSuppression10000,
        "explicit-barrier" or "explicit-barrier4096" => GraphicsWorkload.ExplicitBarrier4096,
        "three-queue-present" => GraphicsWorkload.ThreeQueuePresent,
        "representative-frame-serial" => GraphicsWorkload.RepresentativeFrameSerial,
        "representative-frame-parallel" => GraphicsWorkload.RepresentativeFrameParallel,
        _ => throw new BenchmarkUsageException($"Unknown workload '{value}'."),
    };

    private static int[] ParseGraphicsCpuResourceCounts(string? value, BenchmarkCommand command)
    {
        int[] result = value is null
            ? (command == BenchmarkCommand.GraphCpu
                ? [200]
                : [])
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static item =>
                {
                    if (!int.TryParse(
                        item,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int count))
                    {
                        throw new BenchmarkUsageException(
                            $"Invalid graph CPU resource count '{item}'.");
                    }
                    return count;
                })
                .Distinct()
                .Order()
                .ToArray();
        if (command != BenchmarkCommand.GraphCpu && value is not null)
            throw new BenchmarkUsageException("--resource-counts is valid only with graph-cpu.");
        if (command == BenchmarkCommand.GraphCpu &&
            (result.Length == 0 || result.Any(static count => count < 4)))
        {
            throw new BenchmarkUsageException(
                "graph-cpu resource counts must contain at least one value of 4 or more.");
        }
        return result;
    }

    private static AdapterId ParseAdapter(string value)
    {
        string[] parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryUInt64(parts[0], out ulong low) || !TryUInt64(parts[1], out ulong high))
            throw new BenchmarkUsageException("--adapter must be '<low>:<high>' using decimal or 0x-prefixed values.");
        return new AdapterId(low, high);
    }

    private static bool TryUInt64(string value, out ulong result)
    {
        NumberStyles style = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            style = NumberStyles.AllowHexSpecifier;
        }
        return ulong.TryParse(value, style, CultureInfo.InvariantCulture, out result);
    }

    private static int ParsePositive(Dictionary<string, string> values, string key, int fallback)
    {
        string? text = Get(values, key);
        if (text is null)
            return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result <= 0)
            throw new BenchmarkUsageException($"--{key} must be a positive Int32.");
        return result;
    }

    private static int ParseNonNegative(Dictionary<string, string> values, string key, int fallback)
    {
        string? text = Get(values, key);
        if (text is null)
            return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result < 0)
            throw new BenchmarkUsageException($"--{key} must be a non-negative Int32.");
        return result;
    }

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? value : null;

    private static void ValidateKnown(Dictionary<string, string> values)
    {
        string[] known =
        [
            "output", "adapter", "variant", "process-index", "processes", "warmup", "samples",
            "draws", "barriers", "shader-dir", "native-runner", "managed-runner", "input", "profile", "resume", "variants", "workloads", "direct-mode", "resource-counts", "backend",
        ];
        foreach (string key in values.Keys)
        {
            if (!known.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new BenchmarkUsageException($"Unknown option '--{key}'.");
        }
    }

    private static string Normalize(string value) =>
        value.Trim().Replace('_', '-').ToLowerInvariant();

    private static string FullPath(string value) => Path.GetFullPath(value);

    internal static string FindRepositoryRoot(string start)
    {
        DirectoryInfo? current = new(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SomeEngine.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate SomeEngine.slnx from '{start}'.");
    }
}
