namespace DefaultGame;

/// <summary>The sample's scene animation and example interfaces.</summary>
public sealed record SampleSceneOptions
{
    public bool DynamicScene { get; init; } = true;
    public string? ScenePlayback { get; init; }
    public bool SurfacePanel { get; init; } = true;
    public bool WorldNameplates { get; init; } = true;
    public bool HudOverlay { get; init; }
    public bool HudNumbersChange { get; init; } = true;

    public static SampleSceneOptions Parse(string[] args, SampleSceneOptions? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        defaults ??= new SampleSceneOptions();
        string? playback = ReadPlayback(args) ?? defaults.ScenePlayback;
        if (playback is not null and not "motion" and not "materials" and not "camera")
            throw new ArgumentException("--scene-playback requires 'motion', 'materials', or 'camera'.");
        return defaults with
        {
            DynamicScene = ReadBooleanOption(args, ["--dynamic-scene"],
                ["--static-scene", "--no-dynamic-scene"], defaults.DynamicScene),
            ScenePlayback = playback,
            SurfacePanel = ReadBooleanOption(args, ["--surface-panel"], ["--no-surface-panel"], defaults.SurfacePanel),
            WorldNameplates = ReadBooleanOption(args, ["--world-nameplates"], ["--no-world-nameplates"], defaults.WorldNameplates),
            HudOverlay = ReadBooleanOption(args, ["--hud", "--hud-static"], ["--no-hud"], defaults.HudOverlay),
            HudNumbersChange = defaults.HudNumbersChange && !args.Contains("--hud-static", StringComparer.OrdinalIgnoreCase),
        };
    }

    private static bool ReadBooleanOption(string[] args, string[] enabled, string[] disabled, bool defaultValue)
    {
        for (int index = args.Length - 1; index >= 0; index--)
        {
            string argument = args[index];
            if (enabled.Contains(argument, StringComparer.OrdinalIgnoreCase)) return true;
            if (disabled.Contains(argument, StringComparer.OrdinalIgnoreCase)) return false;
        }
        return defaultValue;
    }

    private static string? ReadPlayback(string[] args)
    {
        const string optionName = "--scene-playback";
        string prefix = optionName + "=";
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return arg[prefix.Length..];
            if (!string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase)) continue;
            if (++index >= args.Length)
                throw new ArgumentException($"{optionName} requires a value.");
            return args[index];
        }
        return null;
    }
}
