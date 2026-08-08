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
    string? ResumeDirectory)
{
    internal const string Usage = """
        Usage:
          SomeEngine.Graphics.Benchmarks warp [--output <report.json>] [--native-runner <exe>]
          SomeEngine.Graphics.Benchmarks diagnose --adapter <low>:<high> [--output <report.json>] --native-runner <exe> [--managed-runner <exe>] [--resume <raw-directory>]
          SomeEngine.Graphics.Benchmarks certify --adapter <low>:<high> [--output <report.json>] --native-runner <exe> [--managed-runner <exe>] [--resume <raw-directory>]
          SomeEngine.Graphics.Benchmarks evaluate --input <report.json>
          SomeEngine.Graphics.Benchmarks worker --profile <warp|diagnose|certify> --variant <generic-rhi|interface-rhi|direct-silk> --adapter <low>:<high> --process-index <n> --shader-dir <path> --output <path> [internal count options]
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
            _ => BenchmarkProfile.WarpFunctional,
        };

        int warmup = ParsePositive(values, "warmup", profile switch
        {
            BenchmarkProfile.WarpFunctional => FixedGraphicsProtocol.WarpWarmupFrames,
            BenchmarkProfile.FastDiagnostic => FixedGraphicsProtocol.DiagnosticWarmupFrames,
            BenchmarkProfile.VendorCertification => FixedGraphicsProtocol.WarmupFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int measured = ParsePositive(values, "samples", profile switch
        {
            BenchmarkProfile.WarpFunctional => FixedGraphicsProtocol.WarpMeasuredFrames,
            BenchmarkProfile.FastDiagnostic => FixedGraphicsProtocol.DiagnosticMeasuredFrames,
            BenchmarkProfile.VendorCertification => FixedGraphicsProtocol.MeasuredFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int draws = ParsePositive(values, "draws", profile switch
        {
            BenchmarkProfile.WarpFunctional => FixedGraphicsProtocol.WarpDrawCount,
            BenchmarkProfile.FastDiagnostic => FixedGraphicsProtocol.DiagnosticDrawCount,
            BenchmarkProfile.VendorCertification => FixedGraphicsProtocol.DrawCount,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        });
        int barriers = profile == BenchmarkProfile.FastDiagnostic
            ? ParseNonNegative(values, "barriers", FixedGraphicsProtocol.DiagnosticBarrierCount)
            : ParsePositive(
                values,
                "barriers",
                profile == BenchmarkProfile.VendorCertification
                    ? FixedGraphicsProtocol.BarrierCount
                    : FixedGraphicsProtocol.WarpBarrierCount);
        int processIndex = ParseNonNegative(values, "process-index", 0);

        ValidateKnown(values);
        if (command == BenchmarkCommand.Worker && (variant is null || adapterText is null))
            throw new BenchmarkUsageException("worker requires --variant and --adapter.");
        if (command is BenchmarkCommand.Certify or BenchmarkCommand.Diagnose && adapterText is null)
            throw new BenchmarkUsageException($"{Normalize(args[0])} requires an explicit hardware --adapter LUID.");
        if (command is BenchmarkCommand.Certify or BenchmarkCommand.Diagnose && Get(values, "native-runner") is null)
            throw new BenchmarkUsageException($"{Normalize(args[0])} requires --native-runner; C++ comparison data cannot be omitted.");
        if (command == BenchmarkCommand.Evaluate && Get(values, "input") is null)
            throw new BenchmarkUsageException("evaluate requires --input.");
        if (Get(values, "resume") is not null &&
            command is not BenchmarkCommand.Certify and not BenchmarkCommand.Diagnose)
        {
            throw new BenchmarkUsageException("--resume is valid only with diagnose or certify.");
        }
        if (Get(values, "managed-runner") is not null &&
            command is not BenchmarkCommand.Certify and not BenchmarkCommand.Diagnose)
        {
            throw new BenchmarkUsageException("--managed-runner is valid only with diagnose or certify.");
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
            Get(values, "resume") is string resume ? FullPath(resume) : null);
    }

    private static BenchmarkCommand ParseCommand(string value) => Normalize(value) switch
    {
        "warp" => BenchmarkCommand.Warp,
        "diagnose" => BenchmarkCommand.Diagnose,
        "certify" => BenchmarkCommand.Certify,
        "worker" => BenchmarkCommand.Worker,
        "evaluate" => BenchmarkCommand.Evaluate,
        _ => throw new BenchmarkUsageException($"Unknown command '{value}'."),
    };

    private static BenchmarkProfile ParseProfile(string value) => Normalize(value) switch
    {
        "warp" => BenchmarkProfile.WarpFunctional,
        "diagnose" => BenchmarkProfile.FastDiagnostic,
        "certify" => BenchmarkProfile.VendorCertification,
        _ => throw new BenchmarkUsageException($"Unknown worker profile '{value}'."),
    };

    private static ReceiverVariant ParseVariant(string value) => Normalize(value) switch
    {
        "generic-rhi" => ReceiverVariant.GenericRhi,
        "interface-rhi" => ReceiverVariant.InterfaceRhi,
        "direct-silk" => ReceiverVariant.DirectSilk,
        "native-cpp" => ReceiverVariant.NativeCpp,
        _ => throw new BenchmarkUsageException($"Unknown receiver variant '{value}'."),
    };

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
            "draws", "barriers", "shader-dir", "native-runner", "managed-runner", "input", "profile", "resume",
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
