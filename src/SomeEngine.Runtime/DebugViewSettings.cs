namespace SomeEngine.Runtime;

/// <summary>An independent diagnostic camera output beside the normal main view.</summary>
public sealed record DebugViewSettings
{
    public DebugViewSettings(uint mode, int width = 320, int height = 180)
    {
        if (mode is < 8 or > 11) throw new ArgumentOutOfRangeException(nameof(mode), "Debug modes are 8 (barycentrics), 9 (normal), 10 (UV), and 11 (deviation).");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Mode = mode;
        Width = width;
        Height = height;
    }
    public uint Mode { get; }
    public int Width { get; }
    public int Height { get; }
}
