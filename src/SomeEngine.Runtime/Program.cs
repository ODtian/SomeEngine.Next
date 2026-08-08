namespace SomeEngine.Runtime;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("SomeEngine.Runtime currently requires Direct3D 12 on Windows.");

            RuntimeStartupOptions options = RuntimeStartupOptions.Parse(args);
            if (options.SkipSwapchainPresent)
            {
                throw new ArgumentException(
                    "The standard runtime always presents its acquired swapchain image; " +
                    "--skip-present is not a supported startup path.");
            }

            bool useWarp = args.Any(static value =>
                string.Equals(value, "--warp", StringComparison.OrdinalIgnoreCase));
            await RuntimeApplication.RunAsync(options, useWarp).ConfigureAwait(false);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
    }
}
