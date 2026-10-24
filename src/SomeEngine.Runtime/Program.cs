namespace SomeEngine.Runtime;

public static class Application
{
    public static Task<int> RunAsync(string[] args, Func<SomeEngine.Resources.Material, float, bool>? animateMaterial = null, ApplicationScene? application = null)
        => RunAsync<EngineSceneMaterials>(args, animateMaterial, application);

    public static async Task<int> RunAsync<TMaterials>(string[] args, Func<SomeEngine.Resources.Material, float, bool>? animateMaterial = null,
        ApplicationScene? application = null) where TMaterials : ISceneMaterials
        => (await RunCoreAsync<TMaterials>(args, animateMaterial, application, null).ConfigureAwait(false)).Succeeded ? 0 : 1;

    /// <summary>Runs application callbacks and returns their completed frame result.</summary>
    public static Task<ApplicationResult> RunAsync(string[] args, ApplicationScene scene) =>
        RunCoreAsync<EngineSceneMaterials>(args, null, scene, null);

    /// <summary>Runs a script through the application frame loop with a software adapter and deterministic frames by default.</summary>
    public static Task<ApplicationResult> RunAsync(string[] args, SceneScript script,
        Func<SomeEngine.Resources.Material, float, bool>? animateMaterial = null)
        => RunAsync<EngineSceneMaterials>(args, script, animateMaterial);

    public static Task<ApplicationResult> RunAsync<TMaterials>(string[] args, SceneScript script,
        Func<SomeEngine.Resources.Material, float, bool>? animateMaterial = null) where TMaterials : ISceneMaterials
    {
        ArgumentNullException.ThrowIfNull(script);
        string[] defaults = ["--hidden-window", "--synchronous-frames",
            "--static-scene", "--no-debug-ui", "--no-vsync"];
        bool hasFixedStep = args.Any(value => value.Equals("--fixed-step", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("--fixed-step=", StringComparison.OrdinalIgnoreCase));
        string[] timing = hasFixedStep ? [] : ["--fixed-step", "0.016666667"];
        return RunCoreAsync<TMaterials>([.. defaults, .. timing, .. args], animateMaterial, null, script);
    }

    private static async Task<ApplicationResult> RunCoreAsync<TMaterials>(string[] args,
        Func<SomeEngine.Resources.Material, float, bool>? animateMaterial, ApplicationScene? application, SceneScript? script)
        where TMaterials : ISceneMaterials
    {
        using var startup = new ApplicationStartupTiming(System.Diagnostics.Stopwatch.GetTimestamp());
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("SomeEngine.Runtime currently requires Direct3D 12 on Windows.");

            RuntimeStartupOptions options = application?.Options ?? RuntimeStartupOptions.Parse(args);
            if (script is not null) options = options with { FrameLimit = script.FrameCount, SynchronousFrames = script.WaitForFrames };
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
            startup.StartTrace(application?.StartupTraceOutput);
            int frames = await RuntimeApplication.RunAsync<TMaterials>(options, useWarp, startup, animateMaterial, application ?? script?.Application, script).ConfigureAwait(false);
            return new(ApplicationFailure.None, frames) { Startup = startup.Result };
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            if (failure is SomeEngine.Graphics.GraphicsException { Diagnostic: { Length: > 0 } diagnostic })
                Console.Error.WriteLine(diagnostic);
            int frames = failure is ApplicationRunException run ? run.CompletedFrames : 0;
            return new(ApplicationRunException.Classify(failure, ApplicationFailure.Startup), frames, failure) { Startup = startup.Result };
        }
    }
}
