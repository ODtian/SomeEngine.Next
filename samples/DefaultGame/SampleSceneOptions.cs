namespace DefaultGame;

/// <summary>The sample's scene animation and example interfaces.</summary>
public sealed record SampleSceneOptions
{
    public bool DynamicScene { get; init; } = true;
    public string? ScenePlayback { get; init; }
    public bool SurfacePanel { get; init; }
    public bool WorldNameplates { get; init; }
    public bool HudOverlay { get; init; }
    public bool HudNumbersChange { get; init; } = true;

    public static SampleSceneOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? playback = ReadPlayback(args);
        if (playback is not null and not "motion" and not "materials" and not "camera")
            throw new ArgumentException("--scene-playback requires 'motion', 'materials', or 'camera'.");
        return new SampleSceneOptions
        {
            DynamicScene = ReadDynamicScene(args),
            ScenePlayback = playback,
            SurfacePanel = args.Contains("--surface-panel", StringComparer.OrdinalIgnoreCase),
            WorldNameplates = args.Contains("--world-nameplates", StringComparer.OrdinalIgnoreCase),
            HudOverlay = args.Contains("--hud", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("--hud-static", StringComparer.OrdinalIgnoreCase),
            HudNumbersChange = !args.Contains("--hud-static", StringComparer.OrdinalIgnoreCase),
        };
    }

    private static bool ReadDynamicScene(string[] args)
    {
        for (int index = args.Length - 1; index >= 0; index--)
        {
            string argument = args[index];
            if (argument.Equals("--dynamic-scene", StringComparison.OrdinalIgnoreCase)) return true;
            if (argument.Equals("--static-scene", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--no-dynamic-scene", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
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
