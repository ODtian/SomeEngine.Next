using System.Globalization;

namespace SomeEngine.Graphics.Benchmarks;

internal sealed class BenchmarkUsageException(string message) : Exception(message);

internal sealed record BenchmarkOptions(
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
    int[] GraphicsCpuResourceCounts)
{
    internal const string Usage = """
        Usage:
          SomeEngine.Graphics.Benchmarks warp [--output <report.json>] [--native-runner <exe>]
          SomeEngine.Graphics.Benchmarks probe --adapter <low>:<high> [--workloads <name,...>] [--variants <interface-receiver,direct-silk,direct-silk-default>] [--output <report.json>] [--resume <raw-directory>]
          SomeEngine.Graphics.Benchmarks diagnose --adapter <low>:<high> [--direct-mode <optimized|default>] [--output <report.json>] --native-runner <exe> [--managed-runner <exe>] [--resume <raw-directory>]
          SomeEngine.Graphics.Benchmarks certify --adapter <low>:<high> [--output <report.json>] --native-runner <exe> [--managed-runner <exe>] [--resume <raw-directory>]
          SomeEngine.Graphics.Benchmarks evaluate --input <report.json>
          SomeEngine.Graphics.Benchmarks graph-cpu --adapter <low>:<high> [--resource-counts <25,50,...,200>] [--output <report.json>] [--warmup <minimum-frames>] [--samples <frames>]
          SomeEngine.Graphics.Benchmarks worker --profile <warp|probe|diagnose|certify|representative> --variant <interface-receiver|direct-silk|direct-silk-default> --adapter <low>:<high> --process-index <n> --shader-dir <path> --output <path> [--direct-mode <optimized|default>] [internal count options]
        """;

    internal static BenchmarkOptions Parse(string[] args)
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
                    BenchmarkCommand.Certify => "vendor-certification.json",
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
            BenchmarkCommand.Certify => BenchmarkProfile.VendorCertification,
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
            BenchmarkProfile.WarpFunctional => FixedGraphicsProtocol.WarpWarmupFrames,
            BenchmarkProfile.FastDiagnostic => FixedGraphicsProtocol.DiagnosticWarmupFrames,
            BenchmarkProfile.VendorCertification => FixedGraphicsProtocol.WarmupFrames,
            BenchmarkProfile.DeveloperProbe => FixedGraphicsProtocol.ProbeWarmupFrames,
            BenchmarkProfile.RepresentativeCpuFrame => FixedGraphicsProtocol.RepresentativeWarmupFrames,
            BenchmarkProfile.GraphicsCpuDevelopment => FixedGraphicsProtocol.GraphicsCpuMinimumWarmupFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int measured = ParsePositive(values, "samples", profile switch
        {
            BenchmarkProfile.WarpFunctional => FixedGraphicsProtocol.WarpMeasuredFrames,
            BenchmarkProfile.FastDiagnostic => FixedGraphicsProtocol.DiagnosticMeasuredFrames,
            BenchmarkProfile.VendorCertification => FixedGraphicsProtocol.MeasuredFrames,
            BenchmarkProfile.DeveloperProbe => FixedGraphicsProtocol.ProbeMeasuredFrames,
            BenchmarkProfile.RepresentativeCpuFrame => FixedGraphicsProtocol.RepresentativeMeasuredFrames,
            BenchmarkProfile.GraphicsCpuDevelopment => FixedGraphicsProtocol.GraphicsCpuMeasuredFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int draws = ParsePositive(values, "draws", profile switch
        {
            BenchmarkProfile.WarpFunctional => FixedGraphicsProtocol.WarpDrawCount,
            BenchmarkProfile.FastDiagnostic => FixedGraphicsProtocol.DiagnosticDrawCount,
            BenchmarkProfile.VendorCertification => FixedGraphicsProtocol.DrawCount,
            BenchmarkProfile.DeveloperProbe => FixedGraphicsProtocol.ProbeDrawCount,
            BenchmarkProfile.RepresentativeCpuFrame => RepresentativeFrameProfile.DrawCount,
            BenchmarkProfile.GraphicsCpuDevelopment => RepresentativeFrameProfile.DrawCount,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int barriers = profile == BenchmarkProfile.FastDiagnostic
            ? ParseNonNegative(values, "barriers", FixedGraphicsProtocol.DiagnosticBarrierCount)
            : ParsePositive(
                values,
                "barriers",
                profile == BenchmarkProfile.VendorCertification
                    ? FixedGraphicsProtocol.BarrierCount
                    : profile == BenchmarkProfile.DeveloperProbe
                        ? FixedGraphicsProtocol.ProbeBarrierCount
                        : profile == BenchmarkProfile.RepresentativeCpuFrame
                            ? RepresentativeFrameProfile.BarrierCount
                            : profile == BenchmarkProfile.GraphicsCpuDevelopment
                                ? RepresentativeFrameProfile.BarrierCount
                            : FixedGraphicsProtocol.WarpBarrierCount);
        int processIndex = ParseNonNegative(values, "process-index", 0);
        string? directMode = Get(values, "direct-mode");
        bool defaultDirectCalls = Normalize(directMode ?? "optimized") switch
        {
            "optimized" => false,
            "default" => true,
            _ => throw new BenchmarkUsageException(
                "--direct-mode must be either 'optimized' or 'default'."),
        };

        ValidateKnown(values);
        if (command == BenchmarkCommand.Worker && (variant is null || adapterText is null))
            throw new BenchmarkUsageException("worker requires --variant and --adapter.");
        if (command is BenchmarkCommand.Certify or BenchmarkCommand.Diagnose or BenchmarkCommand.Probe or BenchmarkCommand.GraphCpu && adapterText is null)
            throw new BenchmarkUsageException($"{Normalize(args[0])} requires an explicit hardware --adapter LUID.");
        if (command is BenchmarkCommand.Certify or BenchmarkCommand.Diagnose && Get(values, "native-runner") is null)
            throw new BenchmarkUsageException($"{Normalize(args[0])} requires --native-runner; C++ comparison data cannot be omitted.");
        if (command == BenchmarkCommand.Evaluate && Get(values, "input") is null)
            throw new BenchmarkUsageException("evaluate requires --input.");
        if (Get(values, "resume") is not null &&
            command is not BenchmarkCommand.Certify and not BenchmarkCommand.Diagnose and not BenchmarkCommand.Probe)
        {
            throw new BenchmarkUsageException("--resume is valid only with probe, diagnose, or certify.");
        }
        if (Get(values, "managed-runner") is not null &&
            command is not BenchmarkCommand.Certify and not BenchmarkCommand.Diagnose)
        {
            throw new BenchmarkUsageException("--managed-runner is valid only with diagnose or certify.");
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
        if (command == BenchmarkCommand.Certify &&
            (warmup != FixedGraphicsProtocol.WarmupFrames ||
             measured != FixedGraphicsProtocol.MeasuredFrames ||
             draws != FixedGraphicsProtocol.DrawCount ||
             barriers != FixedGraphicsProtocol.BarrierCount))
        {
            throw new BenchmarkUsageException("The certification protocol counts are fixed and cannot be overridden.");
        }
        if (command == BenchmarkCommand.Diagnose &&
            (warmup != FixedGraphicsProtocol.DiagnosticWarmupFrames ||
             measured != FixedGraphicsProtocol.DiagnosticMeasuredFrames ||
             draws != FixedGraphicsProtocol.DiagnosticDrawCount ||
             barriers != FixedGraphicsProtocol.DiagnosticBarrierCount))
        {
            throw new BenchmarkUsageException("The fast diagnostic counts are fixed and cannot be overridden.");
        }

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
            ParseVariants(Get(values, "variants"), command),
            ParseWorkloads(Get(values, "workloads"), command),
            ParseGraphicsCpuResourceCounts(Get(values, "resource-counts"), command));
    }

    private static BenchmarkCommand ParseCommand(string value) => Normalize(value) switch
    {
        "warp" => BenchmarkCommand.Warp,
        "diagnose" => BenchmarkCommand.Diagnose,
        "certify" => BenchmarkCommand.Certify,
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
        "certify" => BenchmarkProfile.VendorCertification,
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

    private static ReceiverVariant[] ParseVariants(string? value, BenchmarkCommand command)
    {
        ReceiverVariant[] result = value is null
            ? (command == BenchmarkCommand.Probe ? FixedGraphicsProtocol.ProbeVariants : [])
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseVariant).Distinct().ToArray();
        if (command != BenchmarkCommand.Probe && value is not null)
            throw new BenchmarkUsageException("--variants is valid only with probe.");
        if (command == BenchmarkCommand.Probe && (result.Length == 0 || result.Contains(ReceiverVariant.NativeCpp)))
            throw new BenchmarkUsageException("probe requires at least one managed variant; native-cpp is reserved for formal protocols.");
        return result;
    }

    private static GraphicsWorkload[] ParseWorkloads(string? value, BenchmarkCommand command)
    {
        GraphicsWorkload[] result = value is null
            ? (command == BenchmarkCommand.Probe ? FixedGraphicsProtocol.ProbeWorkloads : [])
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
            (result.Length == 0 || result.Any(static count => count < 25 || count > 200 || count % 25 != 0)))
        {
            throw new BenchmarkUsageException(
                "graph-cpu resource counts must be one or more official sweep values: 25,50,75,100,125,150,175,200.");
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
            "output", "adapter", "variant", "process-index", "warmup", "samples",
            "draws", "barriers", "shader-dir", "native-runner", "managed-runner", "input", "profile", "resume", "variants", "workloads", "direct-mode", "resource-counts",
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
