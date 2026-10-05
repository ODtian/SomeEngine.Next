using SomeEngine.Core.Diagnostics;


namespace SomeEngine.Runtime;

public enum RuntimeGraphicsBackend : byte
{
    Direct3D12,
    Vulkan,
}

public sealed record RuntimeStartupOptions(
    int FrameLimit,
    bool WindowVSync,
    bool SkipSwapchainPresent,
    bool VerifyFrameOutput,
    string? RenderDocCapture,
    uint RenderDocFrame,
    bool DeviceValidation,
    RuntimeGraphicsBackend GraphicsBackend,
    bool DynamicScene,
    bool AsyncCompute,
    ProfilerOptions Profiler)
{
    public bool HideWindow { get; init; }
    public bool ForceHardwareRaster { get; init; }
    public bool UseInstanceGroups { get; init; }
    public bool DisableMeshShaders { get; init; }
    public string? TraceOutput { get; init; }
    public bool TraceDetail { get; init; }
    public bool TraceAllocations { get; init; }
    public bool TraceFramesOnly { get; init; }
    public bool SynchronousFrames { get; init; }
    public string? ScenePlayback { get; init; }
    public float FixedFrameSeconds { get; init; }
    public string? FrameOutputPath { get; init; }
    /// <summary>Packed top-down linear RGBA16F material color, before temporal resolve and tone mapping.</summary>
    public string? LinearFrameOutputPath { get; init; }
    /// <summary>Zero-based scene frame to capture; finite runs default to their last frame.</summary>
    public int? CaptureFrame { get; init; }
    public bool HideDebugUi { get; init; }
    public bool TemporalAntialiasing { get; init; } = true;
    public bool VirtualShadows { get; init; } = true;

    public static RuntimeStartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (string argument in args)
        {
            string option = argument.Split('=', 2)[0];
            if (option.Equals("--present-interval", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("--pipeline-budget", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("--wait-pipelines", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("--no-wait-pipelines", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{option} is unsupported. Presentation is controlled by --vsync/--no-vsync; pipelines are created before rendering.");
        }


        bool windowVSync = ReadBooleanOption(
            args,
            enableSwitches: ["--vsync"],
            disableSwitches: ["--no-vsync"],
            defaultValue: true);
        int frameLimit = ReadPositive(args, "--frames", defaultValue: 0);
        string? frameOutputPath = ReadOptionalValue(args, "--frame-output");
        if (frameOutputPath is not null &&
            !Path.GetExtension(frameOutputPath).Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--frame-output requires a .bmp destination.");
        string? linearFrameOutputPath = ReadOptionalValue(args, "--linear-frame-output");
        if (linearFrameOutputPath is not null &&
            !Path.GetExtension(linearFrameOutputPath).Equals(".rgba16", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--linear-frame-output requires a .rgba16 destination.", "--linear-frame-output");
        int? captureFrame = null;
        string? captureText = ReadOptionalValue(args, "--capture-frame");
        if (captureText is not null)
        {
            if (!int.TryParse(captureText, out int parsed) || parsed < 0 || (frameLimit > 0 && parsed >= frameLimit))
                throw new ArgumentOutOfRangeException("--capture-frame");
            if (frameOutputPath is null && linearFrameOutputPath is null)
                throw new ArgumentException("--capture-frame requires an output destination.", "--capture-frame");
            captureFrame = parsed;
        }
        else if (frameLimit > 0 && (frameOutputPath is not null || linearFrameOutputPath is not null))
            captureFrame = frameLimit - 1;
        else if (linearFrameOutputPath is not null)
            throw new ArgumentException("Linear output requires --frames or --capture-frame.", "--linear-frame-output");
        string? playback = ReadOptionalValue(args, "--scene-playback");
        if (playback is not null and not "motion" and not "materials" and not "camera")
            throw new ArgumentException("--scene-playback requires 'motion', 'materials', or 'camera'.");
        bool synchronousFrames = args.Contains("--synchronous-frames", StringComparer.OrdinalIgnoreCase);
        float fixedStep = 0;
        string? stepText = ReadOptionalValue(args, "--fixed-step");
        if (stepText is not null && (!float.TryParse(stepText, System.Globalization.CultureInfo.InvariantCulture, out fixedStep)
            || !float.IsFinite(fixedStep) || fixedStep <= 0))
            throw new ArgumentException("--fixed-step requires a finite positive number of seconds.");

        return new RuntimeStartupOptions(
            FrameLimit: frameLimit,
            WindowVSync: windowVSync,
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
            GraphicsBackend: ParseGraphicsBackend(
                ReadOptionalValue(args, "--graphics-backend") ??
                ReadOptionalValue(args, "--backend") ??
                "d3d12"),
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
            Profiler: SomeEngine.Core.Diagnostics.Profiler.ParseOptions(args))
        {
            HideWindow = args.Contains("--hidden-window", StringComparer.OrdinalIgnoreCase),
            ForceHardwareRaster = args.Contains("--hardware-raster", StringComparer.OrdinalIgnoreCase),
            UseInstanceGroups = args.Contains("--instance-groups", StringComparer.OrdinalIgnoreCase),
            DisableMeshShaders = args.Contains("--no-mesh-shaders", StringComparer.OrdinalIgnoreCase),
            TraceOutput = ReadOptionalValue(args, "--trace"),
            TraceDetail = args.Contains("--trace-detail", StringComparer.OrdinalIgnoreCase),
            TraceAllocations = args.Contains("--trace-allocations", StringComparer.OrdinalIgnoreCase),
            TraceFramesOnly = args.Contains("--trace-frames-only", StringComparer.OrdinalIgnoreCase),
            SynchronousFrames = synchronousFrames,
            ScenePlayback = playback,
            FixedFrameSeconds = fixedStep,
            FrameOutputPath = frameOutputPath,
            LinearFrameOutputPath = linearFrameOutputPath,
            CaptureFrame = captureFrame,
            HideDebugUi = args.Contains("--no-debug-ui", StringComparer.OrdinalIgnoreCase),
            TemporalAntialiasing = ReadBooleanOption(args, ["--taa"], ["--no-taa"], true),
            VirtualShadows = ReadBooleanOption(args, ["--vsm"], ["--no-vsm"], true),
        };
    }

    private static RuntimeGraphicsBackend ParseGraphicsBackend(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "d3d12" or "direct3d12" => RuntimeGraphicsBackend.Direct3D12,
            "vulkan" => RuntimeGraphicsBackend.Vulkan,
            _ => throw new ArgumentException("--graphics-backend requires 'd3d12' or 'vulkan'."),
        };

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

