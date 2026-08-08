using SomeEngine.Core.Diagnostics;


namespace SomeEngine.Runtime;

public sealed record RuntimeStartupOptions(
    int FrameLimit,
    bool WindowVSync,
    uint PresentSyncInterval,
    double UpdatesPerSecond,
    double FramesPerSecond,
    int PipelineWarmupBudget,
    bool WaitForPipelineWarmup,
    bool SkipSwapchainPresent,
    bool VerifyFrameOutput,
    string? RenderDocCapture,
    uint RenderDocFrame,
    bool DeviceValidation,
    bool DynamicScene,
    bool AsyncCompute,
    int BenchmarkWarmupFrames,
    int BenchmarkSampleFrames,
    string? BenchmarkOutput,
    ProfilerOptions Profiler)
{
    public bool BenchmarkEnabled => BenchmarkOutput is not null;
    public bool BenchmarkOuterOnly { get; init; }

    public static RuntimeStartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool windowVSync = ReadBooleanOption(
            args,
            enableSwitches: ["--vsync"],
            disableSwitches: ["--no-vsync"],
            defaultValue: true);
        string? benchmarkOutput = ReadOptionalValue(args, "--benchmark-output");
        int benchmarkWarmup = ReadPositive(
            args,
            "--benchmark-warmup",
            defaultValue: 8_192);
        int benchmarkSamples = ReadPositive(
            args,
            "--benchmark-samples",
            defaultValue: 16_384);
        int frameLimit = benchmarkOutput is null
            ? ReadPositive(args, "--frames", defaultValue: 0)
            : checked(
                benchmarkWarmup +
                benchmarkSamples +
                ((benchmarkSamples & 1) == 0 ? 2 : 1));

        return new RuntimeStartupOptions(
            FrameLimit: frameLimit,
            WindowVSync: windowVSync,
            PresentSyncInterval: ReadSync(args, windowVSync),
            UpdatesPerSecond: windowVSync ? 60.0 : 0.0,
            FramesPerSecond: windowVSync ? 60.0 : 0.0,
            PipelineWarmupBudget: ReadPositive(args, "--pipeline-budget", defaultValue: 4),
            WaitForPipelineWarmup: ReadBooleanOption(
                args,
                enableSwitches: ["--wait-pipelines"],
                disableSwitches: ["--no-wait-pipelines"],
                defaultValue: true),
            SkipSwapchainPresent: ReadBooleanOption(
                args,
                enableSwitches: ["--skip-present"],
                disableSwitches: ["--no-skip-present"],
                defaultValue: false),
            VerifyFrameOutput: ReadBooleanOption(
                args,
                enableSwitches: ["--verify-frame-output"],
                disableSwitches: ["--no-verify-frame-output"],
                defaultValue: false),
            RenderDocCapture: ReadOptionalValue(args, "--renderdoc-capture"),
            RenderDocFrame: checked((uint)ReadPositive(args, "--renderdoc-frame", defaultValue: 1)),
            DeviceValidation: ReadBooleanOption(
                args,
                enableSwitches: ["--gpu-validation", "--rhi-validation"],
                disableSwitches: ["--no-gpu-validation", "--no-rhi-validation"],
                defaultValue: false),
            DynamicScene: ReadBooleanOption(
                args,
                enableSwitches: ["--dynamic-scene"],
                disableSwitches: ["--static-scene", "--no-dynamic-scene"],
                defaultValue: true),
            AsyncCompute: ReadBooleanOption(
                args,
                enableSwitches: ["--async-compute"],
                disableSwitches: ["--no-async-compute"],
                defaultValue: true),
            BenchmarkWarmupFrames: benchmarkWarmup,
            BenchmarkSampleFrames: benchmarkSamples,
            BenchmarkOutput: benchmarkOutput,
            Profiler: SomeEngine.Core.Diagnostics.Profiler.ParseOptions(args))
        {
            BenchmarkOuterOnly = ReadBooleanOption(
                args,
                enableSwitches: ["--benchmark-outer-only"],
                disableSwitches: ["--benchmark-breakdown"],
                defaultValue: false),
        };
    }

    private static int ReadPositive(string[] args, string optionName, int defaultValue)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (++index >= args.Length)
                throw new ArgumentException($"{optionName} requires a positive integer value.");

            if (!int.TryParse(args[index], out int parsed) || parsed <= 0)
                throw new ArgumentException($"{optionName} requires a positive integer value.");

            return parsed;
        }

        return defaultValue;
    }

    private static uint ReadSync(string[] args, bool windowVSync)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            const string presentIntervalPrefix = "--present-interval=";
            if (arg.StartsWith(presentIntervalPrefix, StringComparison.OrdinalIgnoreCase))
                return ParsePresentSyncInterval("--present-interval", arg[presentIntervalPrefix.Length..]);

            if (!string.Equals(arg, "--present-interval", StringComparison.OrdinalIgnoreCase))
                continue;

            if (++index >= args.Length)
                throw new ArgumentException("--present-interval requires an integer value from 0 to 4.");

            return ParsePresentSyncInterval("--present-interval", args[index]);
        }

        return windowVSync ? 1u : 0u;
    }

    private static uint ParsePresentSyncInterval(string name, string value)
    {
        if (!uint.TryParse(value, out uint interval) || interval > 4)
            throw new ArgumentException($"{name} requires an integer value from 0 to 4.");

        return interval;
    }
    private static string? ReadOptionalValue(string[] args, string optionName)
    {
        string prefix = optionName + "=";
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return arg[prefix.Length..];

            if (!string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (++index >= args.Length)
                throw new ArgumentException($"{optionName} requires a value.");

            return args[index];
        }

        return null;
    }

    private static bool ReadBooleanOption(
        string[] args,
        IReadOnlyList<string> enableSwitches,
        IReadOnlyList<string> disableSwitches,
        bool defaultValue)
    {
        for (int index = args.Length - 1; index >= 0; index--)
        {
            string arg = args[index];
            if (Contains(enableSwitches, arg))
                return true;
            if (Contains(disableSwitches, arg))
                return false;
        }

        return defaultValue;
    }

    private static bool Contains(IReadOnlyList<string> values, string candidate)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

