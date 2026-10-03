namespace SomeEngine.Runtime;

public static class Application
{
    public static async Task<int> RunAsync(string[] args, Func<SomeEngine.Resources.Material, float, bool>? animateMaterial = null, SceneTestPlatform? platform = null)
        => (await RunCoreAsync(args, animateMaterial, platform, null).ConfigureAwait(false)).Succeeded ? 0 : 1;

    /// <summary>Runs a script through the application frame loop with a software adapter and deterministic frames by default.</summary>
    public static Task<ApplicationResult> RunAsync(string[] args, SceneScript script,
        Func<SomeEngine.Resources.Material, float, bool>? animateMaterial = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        string[] defaults = ["--hidden-window", "--synchronous-frames",
            "--static-scene", "--no-debug-ui", "--no-vsync"];
        bool hasFixedStep = args.Any(value => value.Equals("--fixed-step", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("--fixed-step=", StringComparison.OrdinalIgnoreCase));
        string[] timing = hasFixedStep ? [] : ["--fixed-step", "0.016666667"];
        return RunCoreAsync([.. defaults, .. timing, .. args], animateMaterial, null, script);
    }

    private static async Task<ApplicationResult> RunCoreAsync(string[] args,
        Func<SomeEngine.Resources.Material, float, bool>? animateMaterial, SceneTestPlatform? platform, SceneScript? script)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("SomeEngine.Runtime currently requires Direct3D 12 on Windows.");

            RuntimeStartupOptions options = RuntimeStartupOptions.Parse(args);
            if (script is not null) options = options with { FrameLimit = script.FrameCount };
            using var trace = options.TraceOutput is null ? null : SomeEngine.Diagnostics.EngineTrace.Start(
                options.TraceOutput, new SomeEngine.Diagnostics.TraceOptions
                {
                    Detail = options.TraceDetail,
                    Allocations = options.TraceAllocations,
                    Scopes = !options.TraceFramesOnly,
                    Metadata = new Dictionary<string, string>
                    {
                        ["application"] = "SomeEngine",
                        ["backend"] = options.GraphicsBackend.ToString(),
                        ["dynamicScene"] = options.DynamicScene.ToString(),
                        ["asyncCompute"] = options.AsyncCompute.ToString(),
                        ["synchronousFrames"] = options.SynchronousFrames.ToString(),
                        ["scenePlayback"] = options.ScenePlayback ?? "none",
                        ["fixedFrameSeconds"] = options.FixedFrameSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["frameLimit"] = options.FrameLimit.ToString(),
                        ["arguments"] = string.Join(" ", args),
                    },
                });
            if (options.SkipSwapchainPresent)
            {
                throw new ArgumentException(
                    "The standard runtime always presents its acquired swapchain image; " +
                    "--skip-present is not a supported startup path.");
            }

            bool useWarp = (script is not null && !args.Contains("--hardware-adapter", StringComparer.OrdinalIgnoreCase)) || args.Any(static value =>
                string.Equals(value, "--warp", StringComparison.OrdinalIgnoreCase));
            int frames = await RuntimeApplication.RunAsync(options, useWarp, animateMaterial, platform, script).ConfigureAwait(false);
            return new(ApplicationFailure.None, frames);
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            if (failure is SomeEngine.Graphics.GraphicsException { Diagnostic: { Length: > 0 } diagnostic })
                Console.Error.WriteLine(diagnostic);
            int frames = failure is ApplicationRunException run ? run.CompletedFrames : 0;
            return new(ApplicationRunException.Classify(failure, ApplicationFailure.Startup), frames, failure);
        }
    }
}
