namespace SomeEngine.Runtime;

public static class Application
{
    public static async Task<int> RunAsync(string[] args, Func<SomeEngine.Resources.Material, float, bool>? animateMaterial = null)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("SomeEngine.Runtime currently requires Direct3D 12 on Windows.");

            RuntimeStartupOptions options = RuntimeStartupOptions.Parse(args);
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

            bool useWarp = args.Any(static value =>
                string.Equals(value, "--warp", StringComparison.OrdinalIgnoreCase));
            await RuntimeApplication.RunAsync(options, useWarp, animateMaterial).ConfigureAwait(false);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            if (failure is SomeEngine.Graphics.GraphicsException { Diagnostic: { Length: > 0 } diagnostic })
                Console.Error.WriteLine(diagnostic);
            return 1;
        }
    }
}
