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
          SomeEngine.Graphics.Benchmarks certify --adapter <low>:<high> [--output <report.json>] --native-runner <exe> [--managed-runner <exe>] [--resume <raw-directory>]
          SomeEngine.Graphics.Benchmarks evaluate --input <report.json>
          SomeEngine.Graphics.Benchmarks worker --profile <warp|certify> --variant <generic-rhi|interface-rhi|direct-silk> --adapter <low>:<high> --process-index <n> --shader-dir <path> --output <path> [internal count options]
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
                command == BenchmarkCommand.Certify ? "vendor-certification.json" : "warp-acceptance.json"));
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
            BenchmarkCommand.Warp => BenchmarkProfile.WarpFunctional,
            _ when string.Equals(Get(values, "profile"), "certify", StringComparison.OrdinalIgnoreCase) =>
                BenchmarkProfile.VendorCertification,
            _ => BenchmarkProfile.WarpFunctional,
        };

        int warmup = ParsePositive(values, "warmup", profile == BenchmarkProfile.VendorCertification
            ? FixedGraphicsProtocol.WarmupFrames
            : FixedGraphicsProtocol.WarpWarmupFrames);
        int measured = ParsePositive(values, "samples", profile == BenchmarkProfile.VendorCertification
            ? FixedGraphicsProtocol.MeasuredFrames
            : FixedGraphicsProtocol.WarpMeasuredFrames);
        int draws = ParsePositive(values, "draws", profile == BenchmarkProfile.VendorCertification
            ? FixedGraphicsProtocol.DrawCount
            : FixedGraphicsProtocol.WarpDrawCount);
        int barriers = ParsePositive(values, "barriers", profile == BenchmarkProfile.VendorCertification
            ? FixedGraphicsProtocol.BarrierCount
            : FixedGraphicsProtocol.WarpBarrierCount);
        int processIndex = ParseNonNegative(values, "process-index", 0);

        ValidateKnown(values);
        if (command == BenchmarkCommand.Worker && (variant is null || adapterText is null))
            throw new BenchmarkUsageException("worker requires --variant and --adapter.");
        if (command == BenchmarkCommand.Certify && adapterText is null)
            throw new BenchmarkUsageException("certify requires an explicit hardware --adapter LUID.");
        if (command == BenchmarkCommand.Certify && Get(values, "native-runner") is null)
            throw new BenchmarkUsageException("certify requires --native-runner; C++ evidence cannot be omitted.");
        if (command == BenchmarkCommand.Evaluate && Get(values, "input") is null)
            throw new BenchmarkUsageException("evaluate requires --input.");
        if (Get(values, "resume") is not null && command != BenchmarkCommand.Certify)
            throw new BenchmarkUsageException("--resume is valid only with certify.");
        if (Get(values, "managed-runner") is not null && command != BenchmarkCommand.Certify)
            throw new BenchmarkUsageException("--managed-runner is valid only with certify.");
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
        "certify" => BenchmarkCommand.Certify,
        "worker" => BenchmarkCommand.Worker,
        "evaluate" => BenchmarkCommand.Evaluate,
        _ => throw new BenchmarkUsageException($"Unknown command '{value}'."),
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
